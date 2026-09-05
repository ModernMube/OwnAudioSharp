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
/// Output channel routing and the plugin delay compensation, both aimed at the inner source.
/// </summary>
public sealed partial class SourceWithEffects : IAudioSource, IMasterClockSource, ISynchronizable
{
    #region Channel Routing (delegated to inner source)

    /// <summary>
    /// Per-source output routing, straight through to the wrapped source. Null when the inner
    /// one isn't a BaseAudioSource - there's nowhere to keep a map then.
    /// </summary>
    public int[]? OutputChannelMapping
    {
        get => _baseInner?.OutputChannelMapping;
        set
        {
            if (_baseInner is null)
            {
                Log.Warning($"[SourceFx] Channel map ignored on source '{Id}': {_innerSource.GetType().Name} has no routing");
                return;
            }

            _baseInner.OutputChannelMapping = value;
        }
    }

    /// <summary>
    /// Fluent shortcut for OutputChannelMapping, hands the wrapper back so the fx chain
    /// can keep being built on it.
    /// </summary>
    /// <param name="channels"></param>
    /// <returns></returns>
    public SourceWithEffects RouteToChannels(params int[] channels)
    {
        OutputChannelMapping = channels;
        return this;
    }

    /// <summary>
    /// Destination indexed routing, straight through to the wrapped source. Same story as
    /// OutputChannelMapping: null when there's nowhere to keep it.
    /// </summary>
    public OutputRoute? OutputRoute
    {
        get => _baseInner?.OutputRoute;
        set
        {
            if (_baseInner is null)
            {
                Log.Warning($"[SourceFx] Output route ignored on source '{Id}': {_innerSource.GetType().Name} has no routing");
                return;
            }

            _baseInner.OutputRoute = value;
        }
    }

    /// <summary>
    /// Fluent shortcut for OutputRoute, hands the wrapper back.
    /// </summary>
    /// <param name="sourceForChannel">source channel per bus channel, -1 for unbound</param>
    /// <param name="gains"></param>
    public SourceWithEffects RouteTo(int[] sourceForChannel, float[]? gains = null)
    {
        OutputRoute = new OutputRoute(sourceForChannel, gains);
        return this;
    }

    #endregion

    #region Plugin Delay Compensation

    /// <summary>
    /// Total chain latency in samples - sum of each effect's LatencySamples. Zero-latency fx add nothing.
    /// Grabs the effects lock briefly, so don't call from the RT thread.
    /// </summary>
    public int EffectLatencySamples
    {
        get
        {
            lock (_effectsLock)
            {
                int _total = 0;
                foreach (var e in _effects) _total += e.LatencySamples;
                return _total;
            }
        }
    }

    /// <summary>
    /// Same sum, but only over the effects actually running. A bypassed lookahead limiter delays
    /// nothing, so this is what an analyzer needs to line the dry and wet signal up - PDC uses the
    /// figure above instead, which stays put across a bypass toggle.
    /// </summary>
    public int ActiveEffectLatencySamples
    {
        get
        {
            lock (_effectsLock)
            {
                int _total = 0;
                foreach (var e in _effects) if (e.Enabled) _total += e.LatencySamples;
                return _total;
            }
        }
    }

    /// <summary>
    /// Sets PDC delay in frames (maxLatency - thisTrackLatency). Allocates a samples*channels ring buffer.
    /// Zero disables it and frees the buffer.
    /// </summary>
    /// <param name="samples"></param>
    [Obsolete("The ring it feeds only runs in the managed ReadSamples path; the rust-native chain renders " +
        "the track natively and never touches it, so this has no audible effect.")]
    public void SetDelayCompensation(int samples)
    {
        _throwIfDisposed();

        if(samples < 0) throw new ArgumentOutOfRangeException(nameof(samples));

        _compensationSamples = samples;

        if (samples > 0)
        {
            _delayBuffer = new float[samples * Config.Channels];
            _delayWritePos = 0;
            _delayReadPos = 0;
        }
        else
            _delayBuffer = null;

        Log.Info($"[SourceFx] PDC on source '{Id}' set to {samples} frames");
    }

    #endregion
}
