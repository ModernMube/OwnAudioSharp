using System;
using System.Collections.Generic;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// <see cref="IAudioEngine"/> implementation backed by the native Rust audio engine
/// (<see cref="RustSafe.AudioEngine"/>).
/// </summary>
/// <remarks>
/// <para>
/// This adapter is the foundation of the phase-3 <c>OwnaudioNET</c> clone: it
/// presents the exact blocking push/pull contract that the existing
/// <see cref="OwnaudioNET.Mixing.AudioEngineWrapper"/> expects
/// (<see cref="Send"/> blocks until buffer space is available, <see cref="Receives"/>
/// pulls captured samples) while internally driving the callback-based Rust streams.
/// </para>
/// <para>
/// The blocking-push world of <see cref="IAudioEngine"/> and the callback-pull world of
/// the Rust streams are bridged by two single-producer/single-consumer
/// <see cref="LockFreeRingBuffer{T}"/> instances: the pump thread writes interleaved
/// output samples through <see cref="Send"/> while the real-time audio callback drains
/// them; for capture the audio callback writes and <see cref="Receives"/> drains.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="Send"/> must be called from a single producer thread
/// (the wrapper pump thread) and <see cref="Receives"/> from a single consumer thread.
/// <see cref="Initialize"/>, <see cref="Start"/>, <see cref="Stop"/> and
/// <see cref="Dispose"/> are expected to be serialized by the caller.
/// </para>
/// </remarks>
internal sealed class RustAudioEngine : IAudioEngine
{
    #region Fields

    private readonly object _stateLock = new();

    private RustSafe.AudioEngine? _engine;
    private RustSafe.AudioOutputStream? _outputStream;
    private RustSafe.AudioInputStream? _inputStream;

    private LockFreeRingBuffer<float>? _outputRing;
    private LockFreeRingBuffer<float>? _inputRing;

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

    #endregion

    #region Properties

    /// <inheritdoc />
    public int FramesPerBuffer => _framesPerBuffer;

    /// <inheritdoc />
    public EngineStatus Status => _status;

    /// <summary>
    /// Gets the underlying native engine, or <see langword="null"/> before initialization or after
    /// disposal. Used by the Rust-native <c>AudioMixer</c> facade to drive a shared
    /// <c>MultiTrackSession</c> output directly on this engine's device.
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
    /// Pauses this engine's own push-based output stream so it does not compete with a
    /// session-driven output opened on the same device (Rust-native mixer facade).
    /// </summary>
    internal void SuspendOutput()
    {
        lock (_stateLock)
        {
            _outputStream?.Pause();
        }
    }

    /// <summary>
    /// Resumes this engine's own push-based output stream previously paused by
    /// <see cref="SuspendOutput"/>, if the engine is running.
    /// </summary>
    internal void ResumeOutput()
    {
        lock (_stateLock)
        {
            if (_running)
            {
                _outputStream?.Play();
            }
        }
    }

    #endregion

    #region IAudioEngine — lifecycle

    /// <inheritdoc />
    public int Initialize(AudioConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException(
                "Invalid audio configuration. Check SampleRate, Channels, BufferSize, and Enable* flags.");

        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RustAudioEngine));

            if (_engine != null)
                return 0;

            try
            {
                _config = config;
                _channels = config.Channels;
                _framesPerBuffer = config.BufferSize;
                _outputEnabled = config.EnableOutput;
                _inputEnabled = config.EnableInput;

                _engine = RustSafe.AudioEngine.Create(MapHostApi(config.HostType));

                if (_outputEnabled)
                {
                    _selectedOutputDevice = FindDevice(_engine.EnumerateOutputDevices(), config.OutputDeviceId, preferOutput: true);
                    _outputRing = new LockFreeRingBuffer<float>(RingCapacity(config));
                    OpenOutputStream(config);
                }

                if (_inputEnabled)
                {
                    _selectedInputDevice = FindDevice(_engine.EnumerateInputDevices(), config.InputDeviceId, preferOutput: false);
                    _inputRing = new LockFreeRingBuffer<float>(RingCapacity(config));
                    OpenInputStream(config);
                }

                _status = EngineStatus.Idle;
                return 0;
            }
            catch (AudioEngineException)
            {
                DisposeNative();
                throw;
            }
            catch (Exception ex)
            {
                DisposeNative();
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
            if (_disposed)
                return -1;
            if (_engine == null)
                return -1;
            if (_running)
                return 0;

            try
            {
                _outputStream?.Play();
                _inputStream?.Play();
                _running = true;
                _status = EngineStatus.Running;
                return 0;
            }
            catch (Exception)
            {
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
            if (_disposed || _engine == null)
                return 0;
            if (!_running)
                return 0;

            try
            {
                _running = false;
                _outputStream?.Pause();
                _inputStream?.Pause();
                _outputRing?.Clear();
                _inputRing?.Clear();
                _status = EngineStatus.Idle;
                return 0;
            }
            catch (Exception)
            {
                _status = EngineStatus.Error;
                return -1;
            }
        }
    }

    #endregion

    #region IAudioEngine — data path

    /// <inheritdoc />
    public void Send(Span<float> samples)
    {
        if (samples.IsEmpty)
            return;

        LockFreeRingBuffer<float>? ring = _outputRing;
        if (ring == null)
            return;

        int offset = 0;
        var spinner = new SpinWait();

        while (offset < samples.Length)
        {
            if (_disposed || !_running || !_outputEnabled)
                return;

            int written = ring.Write(samples.Slice(offset));
            offset += written;

            if (offset < samples.Length)
            {
                // Ring is full; the audio callback has not drained it yet. Back off
                // briefly and retry — this is the blocking semantics IAudioEngine promises.
                spinner.SpinOnce();
            }
            else
            {
                spinner.Reset();
            }
        }
    }

    /// <inheritdoc />
    public int Receives(Span<float> destination)
    {
        LockFreeRingBuffer<float>? ring = _inputRing;
        if (ring == null || destination.IsEmpty)
            return 0;

        return ring.Read(destination);
    }

    private void OutputCallback(in RustSafe.Callbacks.AudioOutputCallbackArgs args)
    {
        // The native buffer is zeroed before the callback, so an underrun simply
        // leaves silence in the unfilled tail.
        _outputRing?.Read(args.Buffer);
    }

    private void InputCallback(in RustSafe.Callbacks.AudioInputCallbackArgs args)
    {
        // Drop captured samples on overflow rather than blocking the audio thread.
        _inputRing?.Write(args.Buffer);
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

    #region IAudioEngine — device enumeration

    /// <inheritdoc />
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        RustSafe.AudioEngine? engine = _engine;
        if (engine == null)
            return new List<AudioDeviceInfo>();

        var result = new List<AudioDeviceInfo>();
        foreach (var device in engine.EnumerateOutputDevices())
        {
            if (device.MaxOutputChannels <= 0)
                continue;
            result.Add(ToDeviceInfo(device, asOutput: true));
        }
        return result;
    }

    /// <inheritdoc />
    public List<AudioDeviceInfo> GetInputDevices()
    {
        RustSafe.AudioEngine? engine = _engine;
        if (engine == null)
            return new List<AudioDeviceInfo>();

        var result = new List<AudioDeviceInfo>();
        foreach (var device in engine.EnumerateInputDevices())
        {
            if (device.MaxInputChannels <= 0)
                continue;
            result.Add(ToDeviceInfo(device, asOutput: false));
        }
        return result;
    }

    /// <inheritdoc />
    public int SetOutputDeviceByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return -1;

        lock (_stateLock)
        {
            if (_engine == null || !_outputEnabled || _config == null)
                return -1;
            if (_running)
                return -1;

            RustSafe.AudioDevice? device = FindDeviceByName(_engine.EnumerateOutputDevices(), deviceName, preferOutput: true);
            if (device == null)
                return -1;

            _selectedOutputDevice = device;
            ReopenOutputStream(_config);
            return 0;
        }
    }

    /// <inheritdoc />
    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        lock (_stateLock)
        {
            if (_engine == null || !_outputEnabled || _config == null)
                return -1;
            if (_running)
                return -1;

            var devices = GetOutputDevices();
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
                return -1;

            return SetOutputDeviceByName(devices[deviceIndex].Name);
        }
    }

    /// <inheritdoc />
    public int SetInputDeviceByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return -1;

        lock (_stateLock)
        {
            if (_engine == null || !_inputEnabled || _config == null)
                return -1;
            if (_running)
                return -1;

            RustSafe.AudioDevice? device = FindDeviceByName(_engine.EnumerateInputDevices(), deviceName, preferOutput: false);
            if (device == null)
                return -1;

            _selectedInputDevice = device;
            ReopenInputStream(_config);
            return 0;
        }
    }

    /// <inheritdoc />
    public int SetInputDeviceByIndex(int deviceIndex)
    {
        lock (_stateLock)
        {
            if (_engine == null || !_inputEnabled || _config == null)
                return -1;
            if (_running)
                return -1;

            var devices = GetInputDevices();
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
                return -1;

            return SetInputDeviceByName(devices[deviceIndex].Name);
        }
    }

    #endregion

    #region IAudioEngine — device events / monitoring

    // The native Rust backend does not yet surface hot-plug events; these are declared
    // to satisfy the interface contract and are never raised. Device monitoring is a
    // no-op for the same reason.
#pragma warning disable CS0067
    /// <inheritdoc />
    public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <inheritdoc />
    public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

    /// <inheritdoc />
    public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <inheritdoc />
    public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;
#pragma warning restore CS0067

    /// <inheritdoc />
    public void PauseDeviceMonitoring()
    {
    }

    /// <inheritdoc />
    public void ResumeDeviceMonitoring()
    {
    }

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
            DisposeNative();
        }
    }

    #endregion

    #region Private helpers

    private void OpenOutputStream(AudioConfig config)
    {
        var streamConfig = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.Channels,
            RustSafe.SampleFormat.F32,
            ClampStreamBuffer(config.BufferSize));

        _outputStream = _engine!.OpenOutputStream(_selectedOutputDevice, streamConfig, OutputCallback);
    }

    private void OpenInputStream(AudioConfig config)
    {
        var streamConfig = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.Channels,
            RustSafe.SampleFormat.F32,
            ClampStreamBuffer(config.BufferSize));

        _inputStream = _engine!.OpenInputStream(_selectedInputDevice, streamConfig, InputCallback);
    }

    private void ReopenOutputStream(AudioConfig config)
    {
        _outputStream?.Dispose();
        _outputStream = null;
        _outputRing?.Clear();
        OpenOutputStream(config);
    }

    private void ReopenInputStream(AudioConfig config)
    {
        _inputStream?.Dispose();
        _inputStream = null;
        _inputRing?.Clear();
        OpenInputStream(config);
    }

    private void DisposeNative()
    {
        try { _outputStream?.Dispose(); } catch { /* best effort */ }
        try { _inputStream?.Dispose(); } catch { /* best effort */ }
        try { _engine?.Dispose(); } catch { /* best effort */ }

        _outputStream = null;
        _inputStream = null;
        _engine = null;
        _outputRing = null;
        _inputRing = null;
    }

    private int RingCapacity(AudioConfig config)
    {
        // Eight engine buffers of head-room decouples the blocking producer from the
        // real-time consumer without unbounded latency.
        long capacity = (long)Math.Max(config.BufferSize, 64) * Math.Max(config.Channels, 1) * 8L;
        return (int)Math.Min(capacity, 1 << 20);
    }

    private static int ClampStreamBuffer(int bufferSize)
    {
        // AudioStreamConfig accepts [16, 8192] frames or 0 (device default). Out-of-range
        // requests fall back to the device default; the ring buffer decouples sizing so
        // the negotiated device buffer need not equal the reported FramesPerBuffer.
        return (bufferSize >= 16 && bufferSize <= 8192) ? bufferSize : 0;
    }

    private static Ownaudio.Audio.HostApi? MapHostApi(EngineHostType hostType) => hostType switch
    {
        EngineHostType.ASIO => Ownaudio.Audio.HostApi.Asio,
        EngineHostType.COREAUDIO => Ownaudio.Audio.HostApi.CoreAudio,
        EngineHostType.ALSA => Ownaudio.Audio.HostApi.Alsa,
        EngineHostType.WASAPI => Ownaudio.Audio.HostApi.Wasapi,
        _ => null,
    };

    private static RustSafe.AudioDevice? FindDevice(
        IReadOnlyList<RustSafe.AudioDevice> devices, string? deviceId, bool preferOutput)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            RustSafe.AudioDevice? byName = FindDeviceByName(devices, deviceId, preferOutput);
            if (byName != null)
                return byName;
        }

        return null; // null selects the system default device in the Rust layer.
    }

    private static RustSafe.AudioDevice? FindDeviceByName(
        IReadOnlyList<RustSafe.AudioDevice> devices, string deviceName, bool preferOutput)
    {
        foreach (var device in devices)
        {
            bool usable = preferOutput ? device.MaxOutputChannels > 0 : device.MaxInputChannels > 0;
            if (usable && string.Equals(device.Name, deviceName, StringComparison.Ordinal))
                return device;
        }
        return null;
    }

    private static AudioDeviceInfo ToDeviceInfo(RustSafe.AudioDevice device, bool asOutput)
    {
        bool isInput = device.MaxInputChannels > 0;
        bool isOutput = device.MaxOutputChannels > 0;
        bool isDefault = asOutput ? device.IsDefaultOutput : device.IsDefaultInput;

        return new AudioDeviceInfo(
            deviceId: device.Name,
            name: device.Name,
            engineName: "RustAudio",
            isInput: isInput,
            isOutput: isOutput,
            isDefault: isDefault,
            state: AudioDeviceState.Active,
            maxInputChannels: device.MaxInputChannels,
            maxOutputChannels: device.MaxOutputChannels);
    }

    #endregion
}
