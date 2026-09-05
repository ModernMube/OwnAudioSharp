using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// Private plumbing: opening, reopening and disposing the native streams, the host-api
/// map and the device lookup helpers.
/// </summary>
internal sealed partial class RustAudioEngine : IAudioEngine
{
    #region Private helpers

    /// <summary>
    /// Opens the playback stream on the selected device.
    /// </summary>
    /// <param name="config"></param>
    private void _openOutputStream(AudioConfig config)
    {
        var _cfg = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.EffectiveOutputChannels,
            RustSafe.SampleFormat.F32,
            _clampStreamBuffer(config.BufferSize),
            _ringFrames(config));

        _outputStream = _engine!.OpenBufferedOutputStream(_selectedOutputDevice, _cfg);

        _openedOutputChannels = _read(() => _outputStream.ChannelCount, config.EffectiveOutputChannels);
        _openedRingFrames = _read(() => _outputStream.RingFrames, 0);
    }

    /// <summary>
    /// Render ring depth in frames from the config's ms. Out of range means "engine default",
    /// which is never what the host meant, so it gets said out loud instead of being taken
    /// quietly. Initialize rejects such a config outright; this catches the config being
    /// mutated afterwards and picked up by a device switch or a restore.
    /// </summary>
    private static int _ringFrames(AudioConfig config)
    {
        int _frames = config.OutputRingFrames;

        if (_frames == 0)
            Log.Warning($"[RustEngine] OutputRingMilliseconds {config.OutputRingMilliseconds} is outside "
                + $"1..{AudioConfig.MaxOutputRingMilliseconds}, falling back to the engine default ring depth");

        return _frames;
    }

    /// <summary>
    /// Opens the capture stream on the selected device.
    /// </summary>
    /// <param name="config"></param>
    private void _openInputStream(AudioConfig config)
    {
        var _cfg = new RustSafe.AudioStreamConfig(
            config.SampleRate,
            config.EffectiveInputChannels,
            RustSafe.SampleFormat.F32,
            _clampStreamBuffer(config.BufferSize));

        _inputStream = _engine!.OpenBufferedInputStream(_selectedInputDevice, _cfg);

        _openedInputChannels = _read(() => _inputStream.ChannelCount, config.EffectiveInputChannels);
    }

    /// <summary>
    /// Reports what the hardware actually granted, next to what was asked for. A device is free
    /// to open wider than requested — CoreAudio offers one width per device, so a 2 in / 4 out
    /// box hands back 4 for a stereo session — and the ring the engine settled on is rarely the
    /// one the config named either. Both are silent adaptations that decide latency and which
    /// physical socket a route reaches, so this is the line to ask for when a host reports
    /// "it plays out of the wrong outputs".
    /// </summary>
    /// <param name="config"></param>
    private void _logOpenedStreams(AudioConfig config)
    {
        string _out = _describeWidth(config.EffectiveOutputChannels, _openedOutputChannels);
        string _in = _inputEnabled ? _describeWidth(config.EffectiveInputChannels, _openedInputChannels) : "off";

        Log.Info($"[RustEngine] Initialized on {config.HostType}: {config.SampleRate}Hz {_out} out / {_in} in, "
            + $"out '{_selectedOutputDevice?.Name ?? "(default)"}' in '{_selectedInputDevice?.Name ?? "(none)"}', "
            + $"buffer {config.BufferSize} frames requested, ring {_describeRing(config)}, "
            + $"latency out/in {_outputStream?.LatencyFrames ?? 0}/{_inputStream?.LatencyFrames ?? 0} frames");
    }

    /// <summary>
    /// "2ch" when the device served the request, "2ch requested -> 4ch opened" when it did not.
    /// </summary>
    /// <param name="requested"></param>
    /// <param name="opened"></param>
    private static string _describeWidth(int requested, int opened) =>
        opened <= 0 || opened == requested ? $"{requested}ch" : $"{requested}ch requested -> {opened}ch opened";

    /// <summary>
    /// The render ring as opened, against the milliseconds it was asked for.
    /// </summary>
    /// <param name="config"></param>
    private string _describeRing(AudioConfig config)
    {
        if (!_outputEnabled) return "off";
        if (_openedRingFrames <= 0) return $"{config.OutputRingMilliseconds}ms requested, engine default";

        double _ms = _openedRingFrames * 1000.0 / Math.Max(1, config.SampleRate);
        return $"{_openedRingFrames} frames ({_ms:F1}ms, {config.OutputRingMilliseconds}ms requested)";
    }

    /// <summary>
    /// Tears the playback stream down and opens it again after a device switch.
    /// </summary>
    /// <param name="config"></param>
    private void _reopenOutputStream(AudioConfig config)
    {
        _outputStream?.Dispose();
        _outputStream = null;
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
        _openInputStream(config);
    }

    /// <summary>
    /// Best effort teardown of everything native we hold.
    /// </summary>
    private void _disposeNative()
    {
        try { _outputStream?.Dispose(); }
        catch (Exception ex) { Log.Error("[RustEngine] Output stream dispose failed", ex); }

        try { _inputStream?.Dispose(); }
        catch (Exception ex) { Log.Error("[RustEngine] Input stream dispose failed", ex); }

        try { _engine?.Dispose(); }
        catch (Exception ex) { Log.Error("[RustEngine] Native engine dispose failed", ex); }

        _outputStream = null;
        _inputStream = null;
        _engine = null;

        //Borrowed, never ours to dispose — the session owns it and dies with us anyway
        _sessionOutputStream = null;
        _sessionInputChannels = 0;
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
