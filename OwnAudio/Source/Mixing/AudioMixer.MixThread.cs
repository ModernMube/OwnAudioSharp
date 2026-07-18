using System;
using System.Threading;
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
            var _args = new AudioErrorEventArgs($"{context} failed (occurrence #{_count}): {ex.Message}", ex);
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (mixer, e) = ((AudioMixer, AudioErrorEventArgs))state!;
                try { mixer.SourceError?.Invoke(mixer, e); } catch { }
            }, (this, _args));
        }
        return _count >= LoopErrorFaultThreshold;
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
