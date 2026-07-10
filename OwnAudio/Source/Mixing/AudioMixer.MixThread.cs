using System;
using System.Threading;
using OwnaudioNET.Events;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Reports a background-loop exception with throttling and decides whether the loop should abort.
    /// </summary>
    /// <remarks>
    /// The mixer's Rust-native control-sync tick previously swallowed every exception and slept, so a
    /// deterministically-repeating fault — a corrupt source, a dropped handle — would throw hundreds
    /// of times a second for hours with the user seeing only "no audio" and no diagnostic. This
    /// surfaces the failure on the mixer's <see cref="AudioMixer.SourceError"/> channel (on the first
    /// occurrence and every <see cref="LoopErrorReportInterval"/> thereafter, dispatched off the loop
    /// thread so a handler can't stall audio) and reports when the consecutive-error run has crossed
    /// <see cref="LoopErrorFaultThreshold"/>, at which point the caller stops the loop.
    /// </remarks>
    /// <param name="context">Short description of the failing loop, for the error message.</param>
    /// <param name="ex">The caught exception.</param>
    /// <param name="consecutive">Consecutive-error counter for that loop; incremented here.</param>
    /// <returns><see langword="true"/> when the loop should abort due to a persistent fault.</returns>
    private bool HandleLoopError(string context, Exception ex, ref int consecutive)
    {
        int count = ++consecutive;
        if (count == 1 || count % LoopErrorReportInterval == 0)
        {
            var args = new AudioErrorEventArgs($"{context} failed (occurrence #{count}): {ex.Message}", ex);
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var (mixer, e) = ((AudioMixer, AudioErrorEventArgs))state!;
                try { mixer.SourceError?.Invoke(mixer, e); } catch { /* handler must not kill the reporter */ }
            }, (this, args));
        }
        return count >= LoopErrorFaultThreshold;
    }

    /// <summary>
    /// Raises the <see cref="AudioMixer.PlaybackEnded"/> event, off the real-time audio path.
    /// </summary>
    internal void RaisePlaybackEnded()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raises the <see cref="AudioMixer.TrackDropout"/> event with the given arguments.
    /// </summary>
    /// <param name="e">The dropout details.</param>
    internal void RaiseTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }
}
