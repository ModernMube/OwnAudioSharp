using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// A bunch of <see cref="AudioTrack"/>s riding one shared sample-accurate clock. Owns the
/// native MultiTrackMixer; PlayAll starts everything in a single native call so they
/// really do begin on the same callback. Disposing kills every track it handed out.
/// Adding tracks and the transport itself sit in the sibling partials.
/// </summary>
public sealed partial class MultiTrackSession : IDisposable
{
    #region Fields

    private readonly MixerHandle _mixerHandle;
    private readonly float _sampleRate;
    private readonly ushort _channels;
    private readonly List<AudioTrack> _tracks = new();
    private readonly IReadOnlyList<AudioTrack> _tracksView;
    private readonly List<FileTrack> _fileTracks = new();
    private readonly List<MemoryTrack> _memoryTracks = new();
    private readonly List<InputTrack> _inputTracks = new();
    private readonly List<CaptureBridge> _captureBridges = new();
    private readonly MasterEffectChain _masterEffects;
    private AudioOutputStream? _outputStream;
    private float _masterGain = 1.0f;
    private float _masterPan = 0.0f;
    private int[] _masterScope = Array.Empty<int>();
    private bool _masterTapping;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Spins up a session at the given output rate and channel count (1 = mono, 2 = stereo).
    /// </summary>
    public MultiTrackSession(float sampleRate, ushort channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;

        int code = OwnAudioNative.ownaudio_v1_mixer_create(sampleRate, channels, out IntPtr rawMixer);
        ErrorCodeMapper.ThrowIfError(code, nameof(MultiTrackSession));

        _mixerHandle = new MixerHandle();
        Marshal.InitHandle(_mixerHandle, rawMixer);

        _tracksView = _tracks.AsReadOnly();
        _masterEffects = new MasterEffectChain(_mixerHandle.DangerousGetHandle());
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// The tracks registered here. Same view instance every time, it wraps the live list.
    /// </summary>
    public IReadOnlyList<AudioTrack> Tracks => _tracksView;

    /// <summary>
    /// Native effect chain over the fully summed mix.
    /// </summary>
    public MasterEffectChain MasterEffects => _masterEffects;

    /// <summary>
    /// Master gain over the summed mix, clamped non-negative. Ramped on the audio thread
    /// so it doesn't click, and it keeps working after OpenOutput moved the mixer there.
    /// </summary>
    public float MasterGain
    {
        get => _masterGain;
        set
        {
            _masterGain = MathF.Max(0f, value);
            if (!_disposed)
            {
                int code = OwnAudioNative.ownaudio_v1_mixer_set_master_gain(
                    _mixerHandle.DangerousGetHandle(),
                    _masterGain);
                ErrorCodeMapper.ThrowIfError(code, nameof(MasterGain));
            }
        }
    }

    /// <summary>
    /// Master pan, -1..+1, equal-power law normalized at center so a centered master
    /// leaves the mix alone. Ramped too.
    /// </summary>
    public float MasterPan
    {
        get => _masterPan;
        set
        {
            _masterPan = Math.Clamp(value, -1.0f, 1.0f);
            if (!_disposed)
            {
                int code = OwnAudioNative.ownaudio_v1_mixer_set_master_pan(
                    _mixerHandle.DangerousGetHandle(),
                    _masterPan);
                ErrorCodeMapper.ThrowIfError(code, nameof(MasterPan));
            }
        }
    }

    #endregion

    #region Output

    /// <summary>
    /// Opens an output stream driven by this session's mixer and starts rendering on the
    /// RT thread — every buffer summed natively, no per-buffer managed callback. Track and
    /// effect changes keep flowing through the mixer handle while it plays. One stream per
    /// session, owned and disposed by it.
    /// </summary>
    /// <param name="engine">The engine owning the output device.</param>
    /// <param name="device">null = system default.</param>
    public AudioOutputStream OpenOutput(Safe.AudioEngine engine, AudioDevice? device = null) =>
        OpenOutput(engine, device, bufferFrames: 0);

    /// <summary>
    /// Same, with the device buffer size spelled out. That is the session's entire output
    /// latency knob: the mixer renders inside the device callback, so unlike a push stream
    /// there is no render ring stacked on top of the buffer to size as well. The driver takes
    /// the size as a request, so read <see cref="AudioOutputStream.CallbackFrames"/> back once
    /// audio runs to see what it granted.
    /// </summary>
    /// <param name="engine">The engine owning the output device.</param>
    /// <param name="device">null = system default.</param>
    /// <param name="bufferFrames">device buffer in frames, 0 lets the platform pick</param>
    public AudioOutputStream OpenOutput(Safe.AudioEngine engine, AudioDevice? device, int bufferFrames)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        if (_outputStream is not null)
            throw new InvalidOperationException("An output stream has already been opened for this session.");

        //Out of what the backend takes falls back to the platform default instead of throwing,
        //so a host passing its own frames-per-buffer can't turn a stray value into a crash
        int _frames = bufferFrames is >= 16 and <= 8192 ? bufferFrames : 0;

        var config = new AudioStreamConfig((int)_sampleRate, _channels, SampleFormat.F32, _frames);
        AudioOutputStream stream = engine.OpenMixerOutputStream(_mixerHandle, device, config);
        stream.Play();

        _outputStream = stream;
        return stream;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Tears down the output stream first so the audio thread lets go of the mixer, then
    /// the source wrappers (their poll timers), then the tracks and the mixer itself.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        StopMasterFxTap();
        _disposed = true;

        _outputStream?.Dispose();
        _outputStream = null;

        foreach (FileTrack fileTrack in _fileTracks) fileTrack.Dispose();
        _fileTracks.Clear();

        foreach (MemoryTrack memoryTrack in _memoryTracks) memoryTrack.Dispose();
        _memoryTracks.Clear();

        foreach (InputTrack inputTrack in _inputTracks) inputTrack.Dispose();
        _inputTracks.Clear();

        foreach (CaptureBridge bridge in _captureBridges) bridge.Dispose();
        _captureBridges.Clear();

        foreach (AudioTrack track in _tracks) track.Dispose();

        _tracks.Clear();
        _mixerHandle.Dispose();
    }

    #endregion

    #region Private helpers

    private void _throwIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MultiTrackSession));
    }

    #endregion
}
