using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// Device enumeration, selection by name or index, and the change notifications.
/// </summary>
internal sealed partial class RustAudioEngine : IAudioEngine
{
    #region IAudioEngine — device enumeration

    /// <summary>
    /// ASIO drivers are exclusive
    /// </summary>
    private bool _isAsioHost() => _config?.HostType == EngineHostType.ASIO;

    /// <summary>
    /// Device ids to open with. ASIO loads one driver per process, so both directions
    /// must name the same device; an empty side follows the named one.
    /// </summary>
    private (string? outputId, string? inputId) _resolveDeviceIds(AudioConfig config)
    {
        string? _outputId = config.OutputDeviceId;
        string? _inputId = config.InputDeviceId;

        if (config.HostType != EngineHostType.ASIO || !_outputEnabled || !_inputEnabled)
            return (_outputId, _inputId);

        if (string.IsNullOrEmpty(_outputId)) return (_inputId, _inputId);
        if (string.IsNullOrEmpty(_inputId)) return (_outputId, _outputId);

        if (!string.Equals(_outputId, _inputId, StringComparison.Ordinal))
            throw new AudioEngineException(
                $"ASIO cannot run capture on '{_inputId}' and playback on '{_outputId}': only one "
                + "ASIO driver can be loaded per process. Use the same device for both.");

        return (_outputId, _inputId);
    }

    /// <inheritdoc />
    public int OutputLatencyFrames
    {
        get
        {
            lock (_stateLock)
                return _outputStream is { } _s ? (int)_s.LatencyFrames : 0;
        }
    }

    /// <inheritdoc />
    public int InputLatencyFrames
    {
        get
        {
            lock (_stateLock)
                return _inputStream is { } _s ? (int)_s.LatencyFrames : 0;
        }
    }

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
        {
            Log.Error("[RustEngine] SetOutputDeviceByName got an empty name");
            return -1;
        }

        lock (_stateLock)
        {
            if (_engine == null || !_outputEnabled || _config == null)
            {
                Log.Error($"[RustEngine] Cannot pick output '{deviceName}': engine not initialized or output disabled");
                return -1;
            }

            if (_running)
            {
                Log.Error($"[RustEngine] Cannot pick output '{deviceName}' while running, stop the engine first");
                return -1;
            }

            if (_isAsioHost())
                throw _asioSwitchNotSupported(nameof(AudioConfig.OutputDeviceId));

            RustSafe.AudioDevice? _device = _findDeviceByName(
                _engine.EnumerateOutputDevices(), deviceName, preferOutput: true);
            if (_device == null)
            {
                Log.Error($"[RustEngine] No output device named '{deviceName}'");
                return -1;
            }

            _selectedOutputDevice = _device;
            _reopenOutputStream(_config);
            Log.Info($"[RustEngine] Output stream reopened on '{_device.Name}': "
                + $"{_describeWidth(_config.EffectiveOutputChannels, _openedOutputChannels)}");
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
            {
                Log.Error($"[RustEngine] Output device index {deviceIndex} out of range (0..{_devices.Count - 1})");
                return -1;
            }

            return SetOutputDeviceByName(_devices[deviceIndex].Name);
        }
    }

    /// <inheritdoc />
    public int SetInputDeviceByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("[RustEngine] SetInputDeviceByName got an empty name");
            return -1;
        }

        lock (_stateLock)
        {
            if (_engine == null || !_inputEnabled || _config == null)
            {
                Log.Error($"[RustEngine] Cannot pick input '{deviceName}': engine not initialized or input disabled");
                return -1;
            }

            if (_running)
            {
                Log.Error($"[RustEngine] Cannot pick input '{deviceName}' while running, stop the engine first");
                return -1;
            }

            if (_isAsioHost())
                throw _asioSwitchNotSupported(nameof(AudioConfig.InputDeviceId));

            RustSafe.AudioDevice? _device = _findDeviceByName(
                _engine.EnumerateInputDevices(), deviceName, preferOutput: false);
            if (_device == null)
            {
                Log.Error($"[RustEngine] No input device named '{deviceName}'");
                return -1;
            }

            _selectedInputDevice = _device;
            _reopenInputStream(_config);
            Log.Info($"[RustEngine] Input stream reopened on '{_device.Name}': "
                + $"{_describeWidth(_config.EffectiveInputChannels, _openedInputChannels)}");
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
            {
                Log.Error($"[RustEngine] Input device index {deviceIndex} out of range (0..{_devices.Count - 1})");
                return -1;
            }

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
}
