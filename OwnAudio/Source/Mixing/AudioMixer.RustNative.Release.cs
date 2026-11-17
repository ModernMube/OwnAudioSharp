using System;
using System.Threading;
using System.Threading.Tasks;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

/// <summary>
/// When a removed effect is safe to dispose. The audio thread calls a VST3 effect's process
/// entry straight from the native chain, so a plugin unloaded before that thread let go of it is
/// a crash, not an exception.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Blocks after the removal: the one rendering while it was queued may still be inside the
    /// plugin, the next one drains the queue first thing.
    /// </summary>
    private const ulong ReleaseBlocks = 2;

    /// <summary>
    /// Returns once the audio thread can no longer call into effect: its native twin is off every
    /// chain (a removal a full queue refused is retried on the tick and waited for), and the
    /// output has rendered two whole blocks since. Once off the chains it returns straight away
    /// while no output runs: the next block to render drains the queue before it touches a chain.
    /// An owed removal only goes out while the output drains the queue, so without one it is
    /// waited for until cancellationToken says stop — the effect is still on the chain then, and
    /// disposing it is not safe. Call it after RemoveEffect or RemoveMasterEffect, then dispose.
    /// </summary>
    public async Task WhenEffectReleasedAsync(IEffectProcessor effect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _throwUnlessRustNative(nameof(WhenEffectReleasedAsync));

        int _blockMs = Math.Max(1, _config.BufferSize * 1000 / Math.Max(1, _config.SampleRate));
        ulong? _from = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_rustSessionLock)
            {
                if (_rustSession is null) return;

                if (!_stillOnANativeChain(effect))
                {
                    if (_rustOutputStream is null) return;

                    ulong _blocks = _rustOutputStream.GetLoad().BlockCount;

                    if (_from is not { } from || _blocks < from) _from = _blocks;
                    else if (_blocks >= from + ReleaseBlocks) return;
                }
            }

            await Task.Delay(_blockMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Under _rustSessionLock. A track removal still owed keeps every effect of the session in
    /// doubt, there is no telling which track it carries.
    /// </summary>
    private bool _stillOnANativeChain(IEffectProcessor effect)
    {
        if (_rustSession!.HasRemovalsOwed) return true;

        foreach (var pair in _rustRemovalsOwed)
            if (ReferenceEquals(pair.Managed, effect)) return true;

        foreach (var pair in _rustMasterEffects)
            if (ReferenceEquals(pair.Managed, effect)) return true;

        foreach (var routing in _rustEffectSources)
            foreach (var pair in routing.Pairs)
                if (ReferenceEquals(pair.Managed, effect)) return true;

        return false;
    }
}
