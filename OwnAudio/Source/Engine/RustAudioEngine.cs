using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// Pull-mode engine over the rust/cpal backend: the native side asks for audio, we hand it
/// over from a lock-free ring. Nothing that could block — no lock, no allocation, no GC
/// pause — ever lands on an audio thread. Lifecycle and state live here, the rest in the
/// sibling partials.
/// </summary>
/// <remarks>
/// Send() must come from a single producer and Receives() from a single consumer.
/// Initialize / Start / Stop / Dispose are expected to be serialized by the caller.
/// </remarks>
internal sealed partial class RustAudioEngine : IAudioEngine
{
    #region Fields

    private readonly object _stateLock = new();

    private RustSafe.AudioEngine? _engine;
    private RustSafe.AudioOutputStream? _outputStream;
    private RustSafe.AudioInputStream? _inputStream;

    private AudioConfig? _config;
    private int _channels = 2;
    private int _framesPerBuffer;
    private EngineStatus _status = EngineStatus.Idle;

    private bool _outputEnabled;
    private bool _inputEnabled;
    private volatile bool _running;
    private volatile bool _disposed;

    private RustSafe.AudioDevice? _selectedOutputDevice;
    private RustSafe.AudioDevice? _selectedInputDevice;

    private IReadOnlyList<RustSafe.AudioDevice> _outputDeviceSnapshot = Array.Empty<RustSafe.AudioDevice>();
    private IReadOnlyList<RustSafe.AudioDevice> _inputDeviceSnapshot = Array.Empty<RustSafe.AudioDevice>();

    /// <summary>
    /// The output stream a native session opened on our device, once it took playback off us.
    /// Not ours to dispose — we only read diagnostics off it, so the numbers keep answering in
    /// the mode everyone actually runs in. Guarded by _stateLock.
    /// </summary>
    private RustSafe.AudioOutputStream? _sessionOutputStream;

    /// <summary>
    /// Width the session's shared capture bridge opened at, 0 while no session holds capture.
    /// </summary>
    private int _sessionInputChannels;

    /// <summary>
    /// Last width and ring depth the hardware really gave us, kept across a release so a
    /// diagnostic never has to fall back to guessing from the request.
    /// </summary>
    private int _openedOutputChannels;
    private int _openedInputChannels;
    private int _openedRingFrames;

    #endregion

    #region Properties

    /// <inheritdoc />
    public int FramesPerBuffer => _framesPerBuffer;

    /// <inheritdoc />
    public EngineStatus Status => _status;

    /// <summary>
    /// Capture frames the native ring had to throw away because nobody called Receives() in time.
    /// Cumulative for the life of the stream.
    /// </summary>
    internal long InputOverflowFrames
    {
        get
        {
            lock (_stateLock)
            {
                return (long)(_inputStream?.DroppedFrames ?? 0);
            }
        }
    }

    /// <summary>
    /// The native engine, null before init and after dispose. The Rust-native mixer facade uses it to drive a
    /// shared MultiTrackSession output on this engine's device.
    /// </summary>
    internal RustSafe.AudioEngine? NativeEngine
    {
        get
        {
            lock (_stateLock)
            {
                return _engine;
            }
        }
    }

    /// <summary>
    /// The capture device this engine was pointed at, null when the host default is in use.
    /// The Rust-native mixer opens its own capture and has to land on the same device, which on
    /// ASIO is not optional: a second driver cannot be loaded next to the one already running.
    /// </summary>
    internal RustSafe.AudioDevice? SelectedInputDevice
    {
        get
        {
            lock (_stateLock)
            {
                return _selectedInputDevice;
            }
        }
    }

    /// <summary>
    /// The playback device this engine was pointed at, null when the host default is in use.
    /// Same story as <see cref="SelectedInputDevice"/>: the session opens its own output and has
    /// to land on the device the engine already chose.
    /// </summary>
    internal RustSafe.AudioDevice? SelectedOutputDevice
    {
        get
        {
            lock (_stateLock)
            {
                return _selectedOutputDevice;
            }
        }
    }

    /// <summary>
    /// Closes our own capture so a session driven one can take the device over.
    /// </summary>
    /// <remarks>
    /// The output side only parks its stream, but capture has to be closed outright: a second
    /// capture on a device that already has one gets silence on ASIO4ALL and takes the process
    /// down with FlexASIO. Nothing reads the engine's capture ring in rust-native mode anyway.
    /// </remarks>
    internal void ReleaseInput()
    {
        lock (_stateLock)
        {
            if (_inputStream == null) return;

            _inputStream.Dispose();
            _inputStream = null;
            Log.Info("[RustEngine] Capture released, session takes the device");
        }
    }

    /// <summary>
    /// Closes our own playback stream instead of merely parking it, for the same reason
    /// <see cref="ReleaseInput"/> exists: a paused stream still holds its callback registered
    /// with the driver. On ASIO every extra registered callback is another one walking the
    /// driver's channel buffers, and cpal's silencing step overruns them.
    /// </summary>
    internal void ReleaseOutput()
    {
        lock (_stateLock)
        {
            if (_outputStream == null) return;

            _outputStream.Dispose();
            _outputStream = null;
            Log.Info("[RustEngine] Playback released, session takes the device");
        }
    }

    /// <summary>
    /// Reopens what <see cref="ReleaseOutput"/> closed, so the engine can drive its own push
    /// output again once the session hands the device back. No-op if it was never closed.
    /// </summary>
    internal void RestoreOutput()
    {
        lock (_stateLock)
        {
            if (_disposed || _engine == null || _config == null) return;
            if (!_outputEnabled || _outputStream != null) return;

            if (_isAsioHost()) return;

            _openOutputStream(_config);
            if (_running) _outputStream?.Play();

            Log.Info($"[RustEngine] Playback restored on '{_selectedOutputDevice?.Name ?? "(default)"}'");
        }
    }

    #endregion

    #region IAudioEngine — lifecycle

    /// <inheritdoc />
    public int Initialize(AudioConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate(out string? _invalid))
        {
            Log.Error($"[RustEngine] Invalid audio configuration: {_invalid}");
            throw new AudioEngineException($"Invalid audio configuration: {_invalid}");
        }

        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RustAudioEngine));

            if (_engine != null)
                return 0;

            try
            {
                _config = config;
                _channels = config.EffectiveOutputChannels;
                _framesPerBuffer = config.BufferSize;
                _outputEnabled = config.EnableOutput;
                _inputEnabled = config.EnableInput;

                _engine = RustSafe.AudioEngine.Create(_mapHostApi(config.HostType));

                if (_outputEnabled) _outputDeviceSnapshot = _engine.EnumerateOutputDevices();
                if (_inputEnabled) _inputDeviceSnapshot = _engine.EnumerateInputDevices();

                (string? _outputId, string? _inputId) = _resolveDeviceIds(config);

                // Capture first, always. One ASIO driver feeds both ways, and if output opens
                // ahead of it the buffers come up render-only: callback fires, no error, dead air.
                if (_inputEnabled)
                {
                    _selectedInputDevice = _findDevice(_inputDeviceSnapshot, _inputId, preferOutput: false);
                    _openInputStream(config);
                }

                if (_outputEnabled)
                {
                    _selectedOutputDevice = _findDevice(_outputDeviceSnapshot, _outputId, preferOutput: true);
                    _openOutputStream(config);
                }

                _logOpenedStreams(config);

                _status = EngineStatus.Idle;
                return 0;
            }
            catch (AudioEngineException ex)
            {
                Log.Error("[RustEngine] Initialize failed", ex);
                _disposeNative();
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("[RustEngine] Initialize failed", ex);
                _disposeNative();
                _status = EngineStatus.Error;
                throw new AudioEngineException($"Failed to initialize Rust audio engine: {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc />
    public int Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _engine == null)
            {
                Log.Error($"[RustEngine] Start on a {(_disposed ? "disposed" : "uninitialized")} engine");
                return -1;
            }

            if (_running) return 0;

            try
            {
                _outputStream?.Play();
                _inputStream?.Play();
                _running = true;
                _status = EngineStatus.Running;
                Log.Info("[RustEngine] Streams playing");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("[RustEngine] Start failed, streams left in error state", ex);
                _status = EngineStatus.Error;
                return -1;
            }
        }
    }

    /// <inheritdoc />
    public int Stop()
    {
        lock (_stateLock)
        {
            if (_disposed || _engine == null) return 0;
            if (!_running) return 0;

            try
            {
                _running = false;
                _outputStream?.Pause();
                _inputStream?.Pause();
                _outputStream?.Clear();
                _inputStream?.Clear();
                _status = EngineStatus.Idle;
                Log.Info("[RustEngine] Streams paused, rings cleared");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("[RustEngine] Stop failed, streams may still be live", ex);
                _status = EngineStatus.Error;
                return -1;
            }
        }
    }

    #endregion

    #region IAudioEngine — status helpers

    /// <inheritdoc />
    public IntPtr GetStream() => IntPtr.Zero;

    /// <inheritdoc />
    public int OwnAudioEngineActivate() => _running ? 1 : 0;

    /// <inheritdoc />
    public int OwnAudioEngineStopped() => _running ? 0 : 1;

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _running = false;
            _disposeNative();
            Log.Info("[RustEngine] Disposed");
        }
    }

    #endregion
}
