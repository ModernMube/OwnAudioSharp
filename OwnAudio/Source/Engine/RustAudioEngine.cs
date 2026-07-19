using System;
using System.Collections.Generic;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// IAudioEngine on top of the native Rust engine. Bridges the blocking push/pull contract the wrapper wants
/// to the callback driven Rust streams through two SPSC ring buffers.
/// </summary>
/// <remarks>
/// Send() must come from a single producer (the pump thread) and Receives() from a single consumer.
/// Initialize / Start / Stop / Dispose are expected to be serialized by the caller.
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

    private IReadOnlyList<RustSafe.AudioDevice> _outputDeviceSnapshot = Array.Empty<RustSafe.AudioDevice>();
    private IReadOnlyList<RustSafe.AudioDevice> _inputDeviceSnapshot = Array.Empty<RustSafe.AudioDevice>();

    #endregion

    #region Properties

    /// <inheritdoc />
    public int FramesPerBuffer => _framesPerBuffer;

    /// <inheritdoc />
    public EngineStatus Status => _status;

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
    /// down with FlexASIO. Nothing reads the engine's input ring in rust-native mode anyway.
    /// </remarks>
    internal void ReleaseInput()
    {
        lock (_stateLock)
        {
            _inputStream?.Dispose();
            _inputStream = null;
            _inputRing?.Clear();
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
            _outputStream?.Dispose();
            _outputStream = null;
            _outputRing?.Clear();
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

            _outputRing ??= new LockFreeRingBuffer<float>(_ringCapacity(_config));
            _openOutputStream(_config);
            if (_running) _outputStream?.Play();
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

                _engine = RustSafe.AudioEngine.Create(_mapHostApi(config.HostType));

                if (_outputEnabled) _outputDeviceSnapshot = _engine.EnumerateOutputDevices();
                if (_inputEnabled) _inputDeviceSnapshot = _engine.EnumerateInputDevices();

                if (_outputEnabled)
                {
                    _selectedOutputDevice = _findDevice(_outputDeviceSnapshot, config.OutputDeviceId, preferOutput: true);
                    _outputRing = new LockFreeRingBuffer<float>(_ringCapacity(config));
                    _openOutputStream(config);
                }

                if (_inputEnabled)
                {
                    _selectedInputDevice = _findDevice(_inputDeviceSnapshot, config.InputDeviceId, preferOutput: false);
                    _inputRing = new LockFreeRingBuffer<float>(_ringCapacity(config));
                    _openInputStream(config);
                }

                _status = EngineStatus.Idle;
                return 0;
            }
            catch (AudioEngineException)
            {
                _disposeNative();
                throw;
            }
            catch (Exception ex)
            {
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
            if (_disposed || _engine == null) return -1;
            if (_running) return 0;

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
            if (_disposed || _engine == null) return 0;
            if (!_running) return 0;

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

        LockFreeRingBuffer<float>? _ring = _outputRing;
        if (_ring == null)
            return;

        int _offset = 0;
        var _spinner = new SpinWait();

        while (_offset < samples.Length)
        {
            if (_disposed || !_running || !_outputEnabled)
                return;

            _offset += _ring.Write(samples.Slice(_offset));

            // Ring full, the callback hasn't drained it yet. Back off and retry, this is the
            // blocking behaviour IAudioEngine promises.
            if (_offset < samples.Length) _spinner.SpinOnce();
            else _spinner.Reset();
        }
    }

    /// <inheritdoc />
    public int Receives(Span<float> destination)
    {
        if (_disposed || !_running)
            return -1;

        LockFreeRingBuffer<float>? _ring = _inputRing;
        if (_ring == null || destination.IsEmpty)
            return 0;

        return _ring.Read(destination);
    }

    /// <summary>
    /// RT callback pulling playback samples. The native buffer comes zeroed, so an underrun just leaves silence.
    /// </summary>
    /// <param name="args"></param>
    private void _outputCallback(in RustSafe.Callbacks.AudioOutputCallbackArgs args)
    {
        _outputRing?.Read(args.Buffer);
    }

    /// <summary>
    /// RT callback pushing captured samples. On overflow we drop rather than block the audio thread.
    /// </summary>
    /// <param name="args"></param>
    private void _inputCallback(in RustSafe.Callbacks.AudioInputCallbackArgs args)
    {
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

    /// <summary>
    /// ASIO drivers are exclusive
    /// </summary>
    private bool _isAsioHost() => _config?.HostType == EngineHostType.ASIO;

    /// <inheritdoc />
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        RustSafe.AudioEngine? _eng = _engine;
        if (_eng == null)
            return new List<AudioDeviceInfo>();

        var _devices = _isAsioHost() ? _outputDeviceSnapshot : _eng.EnumerateOutputDevices();

        var _result = new List<AudioDeviceInfo>();
        foreach (var device in _devices)
        {
            if (device.MaxOutputChannels <= 0) continue;
            _result.Add(_toDeviceInfo(device, asOutput: true));
        }
        return _result;
    }

    /// <inheritdoc />
    public List<AudioDeviceInfo> GetInputDevices()
    {
        RustSafe.AudioEngine? _eng = _engine;
        if (_eng == null)
            return new List<AudioDeviceInfo>();

        var _devices = _isAsioHost() ? _inputDeviceSnapshot : _eng.EnumerateInputDevices();

        var _result = new List<AudioDeviceInfo>();
        foreach (var device in _devices)
        {
            if (device.MaxInputChannels <= 0) continue;
            _result.Add(_toDeviceInfo(device, asOutput: false));
        }
        return _result;
    }

    /// <inheritdoc />
    public int SetOutputDeviceByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return -1;

        lock (_stateLock)
        {
            if (_engine == null || !_outputEnabled || _config == null) return -1;
            if (_running) return -1;

            if (_isAsioHost())
                throw _asioSwitchNotSupported(nameof(AudioConfig.OutputDeviceId));

            RustSafe.AudioDevice? _device = _findDeviceByName(
                _engine.EnumerateOutputDevices(), deviceName, preferOutput: true);
            if (_device == null)
                return -1;

            _selectedOutputDevice = _device;
            _reopenOutputStream(_config);
            return 0;
        }
    }

    /// <inheritdoc />
    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        lock (_stateLock)
        {
            if (_engine == null || !_outputEnabled || _config == null) return -1;
            if (_running) return -1;

            var _devices = GetOutputDevices();
            if (deviceIndex < 0 || deviceIndex >= _devices.Count)
                return -1;

            return SetOutputDeviceByName(_devices[deviceIndex].Name);
        }
    }

    /// <inheritdoc />
    public int SetInputDeviceByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return -1;

        lock (_stateLock)
        {
            if (_engine == null || !_inputEnabled || _config == null) return -1;
            if (_running) return -1;

            if (_isAsioHost())
                throw _asioSwitchNotSupported(nameof(AudioConfig.InputDeviceId));

            RustSafe.AudioDevice? _device = _findDeviceByName(
                _engine.EnumerateInputDevices(), deviceName, preferOutput: false);
            if (_device == null)
                return -1;

            _selectedInputDevice = _device;
            _reopenInputStream(_config);
            return 0;
        }
    }

    /// <summary>
    /// Why picking a different ASIO device on a live engine is turned down instead of attempted.
    /// </summary>
    private static NotSupportedException _asioSwitchNotSupported(string configProperty) =>
        new($"Changing the device of a running ASIO engine is not supported — the driver teardown "
            + $"it needs corrupts process memory. Set AudioConfig.{configProperty} before "
            + "Initialize and build a new engine instead.");

    /// <inheritdoc />
    public int SetInputDeviceByIndex(int deviceIndex)
    {
        lock (_stateLock)
        {
            if (_engine == null || !_inputEnabled || _config == null) return -1;
            if (_running) return -1;

            var _devices = GetInputDevices();
            if (deviceIndex < 0 || deviceIndex >= _devices.Count)
                return -1;

            return SetInputDeviceByName(_devices[deviceIndex].Name);
        }
    }

    #endregion

    #region IAudioEngine — device events / monitoring

    // The Rust backend has no hot-plug events yet, these are here for the interface and never fire.
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
            _disposeNative();
        }
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Opens the playback stream on the selected device.
    /// </summary>
    /// <param name="config"></param>
    private void _openOutputStream(AudioConfig config)
    {
        var _cfg = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.Channels,
            RustSafe.SampleFormat.F32,
            _clampStreamBuffer(config.BufferSize));

        _outputStream = _engine!.OpenOutputStream(_selectedOutputDevice, _cfg, _outputCallback);
    }

    /// <summary>
    /// Opens the capture stream on the selected device.
    /// </summary>
    /// <param name="config"></param>
    private void _openInputStream(AudioConfig config)
    {
        var _cfg = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.Channels,
            RustSafe.SampleFormat.F32,
            _clampStreamBuffer(config.BufferSize));

        _inputStream = _engine!.OpenInputStream(_selectedInputDevice, _cfg, _inputCallback);
    }

    /// <summary>
    /// Tears the playback stream down and opens it again after a device switch.
    /// </summary>
    /// <param name="config"></param>
    private void _reopenOutputStream(AudioConfig config)
    {
        _outputStream?.Dispose();
        _outputStream = null;
        _outputRing?.Clear();
        _openOutputStream(config);
    }

    /// <summary>
    /// Tears the capture stream down and opens it again after a device switch.
    /// </summary>
    /// <param name="config"></param>
    private void _reopenInputStream(AudioConfig config)
    {
        _inputStream?.Dispose();
        _inputStream = null;
        _inputRing?.Clear();
        _openInputStream(config);
    }

    /// <summary>
    /// Best effort teardown of everything native we hold.
    /// </summary>
    private void _disposeNative()
    {
        try { _outputStream?.Dispose(); } catch { }
        try { _inputStream?.Dispose(); } catch { }
        try { _engine?.Dispose(); } catch { }

        _outputStream = null;
        _inputStream = null;
        _engine = null;
        _outputRing = null;
        _inputRing = null;
    }

    /// <summary>
    /// Ring size in samples. Eight engine buffers of headroom keeps the blocking producer off the RT
    /// consumer without piling up latency, capped at 1M.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    private int _ringCapacity(AudioConfig config)
    {
        long _capacity = (long)Math.Max(config.BufferSize, 64) * Math.Max(config.Channels, 1) * 8L;
        return (int)Math.Min(_capacity, 1 << 20);
    }

    /// <summary>
    /// AudioStreamConfig takes [16, 8192] frames or 0 for the device default. Anything else falls back to 0,
    /// the ring decouples sizing so the device buffer need not match FramesPerBuffer.
    /// </summary>
    /// <param name="bufferSize"></param>
    /// <returns></returns>
    private static int _clampStreamBuffer(int bufferSize)
        => (bufferSize >= 16 && bufferSize <= 8192) ? bufferSize : 0;

    /// <summary>
    /// Maps our host enum onto the Rust one, null means let cpal decide.
    /// </summary>
    /// <param name="hostType"></param>
    /// <returns></returns>
    private static Ownaudio.Audio.HostApi? _mapHostApi(EngineHostType hostType) => hostType switch
    {
        EngineHostType.ASIO => Ownaudio.Audio.HostApi.Asio,
        EngineHostType.COREAUDIO => Ownaudio.Audio.HostApi.CoreAudio,
        EngineHostType.ALSA => Ownaudio.Audio.HostApi.Alsa,
        EngineHostType.WASAPI => Ownaudio.Audio.HostApi.Wasapi,
        EngineHostType.AAUDIO => Ownaudio.Audio.HostApi.AAudio,
        _ => null,
    };

    /// <summary>
    /// Looks up the configured device id, null result means the Rust layer picks the system default.
    /// preferOutput decides which channel count has to be non-zero.
    /// </summary>
    /// <param name="devices"></param>
    /// <param name="deviceId"></param>
    /// <param name="preferOutput"></param>
    /// <returns></returns>
    private static RustSafe.AudioDevice? _findDevice(
        IReadOnlyList<RustSafe.AudioDevice> devices, string? deviceId, bool preferOutput)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        return _findDeviceByName(devices, deviceId, preferOutput);
    }

    /// <summary>
    /// Exact name match among the usable devices. preferOutput picks which direction counts as usable.
    /// </summary>
    /// <param name="devices"></param>
    /// <param name="deviceName"></param>
    /// <param name="preferOutput"></param>
    /// <returns></returns>
    private static RustSafe.AudioDevice? _findDeviceByName(
        IReadOnlyList<RustSafe.AudioDevice> devices, string deviceName, bool preferOutput)
    {
        foreach (var device in devices)
        {
            bool _usable = preferOutput ? device.MaxOutputChannels > 0 : device.MaxInputChannels > 0;
            if (_usable && string.Equals(device.Name, deviceName, StringComparison.Ordinal))
                return device;
        }
        return null;
    }

    /// <summary>
    /// Converts a Rust device into the core info record. asOutput tells which default flag to report.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="asOutput"></param>
    /// <returns></returns>
    private static AudioDeviceInfo _toDeviceInfo(RustSafe.AudioDevice device, bool asOutput)
    {
        return new AudioDeviceInfo(
            deviceId: device.Name,
            name: device.Name,
            engineName: "RustAudio",
            isInput: device.MaxInputChannels > 0,
            isOutput: device.MaxOutputChannels > 0,
            isDefault: asOutput ? device.IsDefaultOutput : device.IsDefaultInput,
            state: AudioDeviceState.Active,
            maxInputChannels: device.MaxInputChannels,
            maxOutputChannels: device.MaxOutputChannels);
    }

    #endregion
}
