using System.Diagnostics;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Monotonic seconds on this machine. Every timestamp on the wire is one of these, never the
/// wall clock — that one jumps when the OS syncs it.
/// </summary>
internal static class NetworkClock
{
    private static readonly double _tickSeconds = 1.0 / Stopwatch.Frequency;

    public static double Now => Stopwatch.GetTimestamp() * _tickSeconds;
}
