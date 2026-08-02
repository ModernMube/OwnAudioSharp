using System;
using System.Threading;
using Logger;
using OwnaudioNET.Events;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Reports a background-loop blowup on SourceError (1st hit, then every Nth) and tells
    /// the caller whether the streak is long enough to give up. Dispatched off the loop
    /// thread so a slow handler can't stall audio.
    /// </summary>
    /// <param name="context">short name of the failing loop, goes into the message</param>
    /// <param name="ex"></param>
    /// <param name="consecutive">error streak counter of that loop, bumped here</param>
    /// <returns></returns>
    private bool _handleLoopError(string context, Exception ex, ref int consecutive)
    {
        int _count = ++consecutive;
        if (_count == 1 || _count % LoopErrorReportInterval == 0)
        {
            Log.Error($"[Mixer] {context} failed (occurrence #{_count})", ex);

            var _args = new AudioErrorEventArgs($"{context} failed (occurrence #{_count}): {ex.Message}", ex);
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (mixer, e) = ((AudioMixer, AudioErrorEventArgs))state!;
                try { mixer.SourceError?.Invoke(mixer, e); }
                catch (Exception handlerEx) { Log.Error("[Mixer] A SourceError handler threw", handlerEx); }
            }, (this, _args));
        }

        bool _fatal = _count >= LoopErrorFaultThreshold;
        if (_fatal) Log.FatalError($"[Mixer] {context} hit {_count} consecutive errors, the loop gives up", ex);

        return _fatal;
    }

    /// <summary>
    /// Same 1st-then-every-Nth throttling as above for the applies the control tick retries on
    /// every pass. No SourceError here, a stuck one would fire it at tick rate.
    /// </summary>
    /// <param name="what">what we were applying, goes into the message</param>
    /// <param name="id"></param>
    /// <param name="ex"></param>
    private void _logRustApplyError(string what, Guid id, Exception ex)
    {
        int _n = ++_rustApplyErrors;
        if (_n == 1 || _n % LoopErrorReportInterval == 0)
            Log.Error($"[Mixer] {what} for source '{id}' failed (occurrence #{_n})", ex);
    }

    /// <summary>
    /// Fires PlaybackEnded, off the real-time path.
    /// </summary>
    internal void RaisePlaybackEnded()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fires TrackDropout with the given details.
    /// </summary>
    /// <param name="e"></param>
    internal void RaiseTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }
}
