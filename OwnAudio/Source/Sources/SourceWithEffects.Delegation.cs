using System.Runtime.CompilerServices;
using System.Threading;
using Logger;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// The pass-through half: properties, the master clock, the sync group, metering and events
/// all go straight to the inner source. Nothing here touches the effect chain.
/// </summary>
public sealed partial class SourceWithEffects : IAudioSource, IMasterClockSource, ISynchronizable
{
    #region IAudioSource Propertyes (delegated to inner source)

    /// <summary>
    /// The wrapped source. Native mixer facade reaches through this to route fx onto the
    /// track's own native chain instead of reading them out through ReadSamples.
    /// </summary>
    internal IAudioSource InnerSource => _innerSource;

    /// <inheritdoc/>
    public Guid Id => _innerSource.Id;

    /// <inheritdoc/>
    public AudioState State => _innerSource.State;

    /// <inheritdoc/>
    public AudioConfig Config => _innerSource.Config;

    /// <inheritdoc/>
    public AudioStreamInfo StreamInfo => _innerSource.StreamInfo;

    /// <inheritdoc/>
    public float Volume
    {
        get => _innerSource.Volume;
        set => _innerSource.Volume = value;
    }

    /// <inheritdoc/>
    public float Pan
    {
        get => _innerSource.Pan;
        set => _innerSource.Pan = value;
    }

    /// <inheritdoc/>
    public bool Loop
    {
        get => _innerSource.Loop;
        set => _innerSource.Loop = value;
    }

    /// <inheritdoc/>
    public double Position => _innerSource.Position;

    /// <inheritdoc/>
    public double Duration => _innerSource.Duration;

    /// <inheritdoc/>
    public bool IsEndOfStream => _innerSource.IsEndOfStream;

    /// <inheritdoc/>
    public float Tempo
    {
        get => _innerSource.Tempo;
        set => _innerSource.Tempo = value;
    }

    /// <inheritdoc/>
    public float PitchShift
    {
        get => _innerSource.PitchShift;
        set => _innerSource.PitchShift = value;
    }

    #endregion

    #region MasterClock (delegated to inner source)

    /// <summary>
    /// True when the wrapped source can ride a master clock at all. A decorator over a plain
    /// IAudioSource still exposes the clock surface, it just has nothing to hand the calls to.
    /// </summary>
    public bool SupportsMasterClock => _clockInner is not null;

    /// <inheritdoc/>
    public double StartOffset
    {
        get => _clockInner?.StartOffset ?? 0.0;
        set
        {
            if (_clockInner is null) { _warnNoClock(nameof(StartOffset)); return; }
            _clockInner.StartOffset = value;
        }
    }

    /// <inheritdoc/>
    public bool IsAttachedToClock => _clockInner?.IsAttachedToClock ?? false;

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        _throwIfDisposed();

        if (_clockInner is null) { _warnNoClock(nameof(AttachToClock)); return; }
        _clockInner.AttachToClock(clock);
    }

    /// <inheritdoc/>
    public void DetachFromClock() => _clockInner?.DetachFromClock();

    /// <summary>
    /// Clock-aligned read with the fx chain on top. Falls back to a plain read when the inner
    /// source doesn't do timestamps - the effects still run either way.
    /// </summary>
    /// <param name="masterTimestamp"></param>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <param name="result"></param>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        _throwIfDisposed();

        bool _ok = true;

        if (_clockInner is not null)
            _ok = _clockInner.ReadSamplesAtTime(masterTimestamp, buffer, frameCount, out result);
        else
            result = ReadResult.CreateSuccess(_innerSource.ReadSamples(buffer, frameCount));

        result.FramesRead = _runEffectChain(buffer, result.FramesRead);
        return _ok;
    }

    /// <summary>
    /// One line per wrapper about a clock call the inner source can't take.
    /// </summary>
    /// <param name="member"></param>
    private void _warnNoClock(string member)
    {
        Log.Warning($"[SourceFx] {member} ignored on source '{Id}': {_innerSource.GetType().Name} does not ride a master clock");
    }

    #endregion

    #region Sync group (delegated to inner source)

    /// <summary>
    /// True when the wrapped source can be sync-grouped at all. A decorator over a plain
    /// IAudioSource still exposes the surface, it just has nothing to hand the calls to.
    /// </summary>
    public bool SupportsSyncGroup => _syncInner is not null;

    /// <inheritdoc/>
    public long SamplePosition => _syncInner?.SamplePosition ?? 0L;

    /// <inheritdoc/>
    public string? SyncGroupId
    {
        get => _syncInner?.SyncGroupId;
        set
        {
            if (_syncInner is null) { _warnNoSync(nameof(SyncGroupId)); return; }
            _syncInner.SyncGroupId = value;
        }
    }

    /// <inheritdoc/>
    public bool IsSynchronized
    {
        get => _syncInner?.IsSynchronized ?? false;
        set
        {
            if (_syncInner is null) { _warnNoSync(nameof(IsSynchronized)); return; }
            _syncInner.IsSynchronized = value;
        }
    }

    /// <summary>
    /// Snaps the wrapped source back, then drops the effect tails — after a jump they'd
    /// smear audio from the old position over the new one.
    /// </summary>
    /// <param name="samplePosition"></param>
    public void ResyncTo(long samplePosition)
    {
        _throwIfDisposed();

        if (_syncInner is null) { _warnNoSync(nameof(ResyncTo)); return; }

        _syncInner.ResyncTo(samplePosition);
        _resetEffectsAndDelay();
    }

    /// <summary>
    /// One line per wrapper about a sync call the inner source can't take.
    /// </summary>
    /// <param name="member"></param>
    private void _warnNoSync(string member)
    {
        Log.Warning($"[SourceFx] {member} ignored on source '{Id}': {_innerSource.GetType().Name} cannot be sync-grouped");
    }

    #endregion

    #region Metering (delegated to inner source)

    /// <summary>
    /// L/R levels of the wrapped source. Zero for a hand-rolled IAudioSource, which keeps
    /// no meters. Post-fx metering has to come off an EffectTap, not from here — the
    /// native track's peaks are what this reads.
    /// </summary>
    public (float left, float right) OutputLevels => _baseInner?.OutputLevels ?? (0f, 0f);

    /// <summary>
    /// Fires when the wrapped source's position moved noticeably. Throttled by the source.
    /// </summary>
    public event EventHandler? PositionChanged
    {
        add
        {
            if (_baseInner is null) { _warnNoMeters(nameof(PositionChanged)); return; }
            _baseInner.PositionChanged += value;
        }
        remove
        {
            if (_baseInner is not null) _baseInner.PositionChanged -= value;
        }
    }

    /// <summary>
    /// Same idea as _warnNoClock, for the metering surface.
    /// </summary>
    /// <param name="member"></param>
    private void _warnNoMeters(string member)
    {
        Log.Warning($"[SourceFx] {member} ignored on source '{Id}': {_innerSource.GetType().Name} keeps no meters");
    }

    #endregion

    #region Events (delegated to inner source)

    /// <inheritdoc/>
    public event EventHandler<AudioStateChangedEventArgs>? StateChanged
    {
        add => _innerSource.StateChanged += value;
        remove => _innerSource.StateChanged -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
    {
        add => _innerSource.BufferUnderrun += value;
        remove => _innerSource.BufferUnderrun -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<AudioErrorEventArgs>? Error
    {
        add => _innerSource.Error += value;
        remove => _innerSource.Error -= value;
    }

    #endregion
}
