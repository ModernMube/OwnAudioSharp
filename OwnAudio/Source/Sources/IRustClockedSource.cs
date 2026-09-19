using Ownaudio.Audio.Tracks;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// Native-backed sources that ride the shared timeline the way a file does (File/GroupSource):
/// a start offset, a content position that follows tempo, and a project position the master
/// clock is driven from. The mixer seeks, offsets and clocks them all through this one surface.
/// </summary>
internal interface IRustClockedSource : IMasterClockSource
{
    /// <summary>
    /// The native track rendering this source, null on legacy or before the backend exists.
    /// </summary>
    AudioTrack? RustTrack { get; }

    /// <summary>
    /// Project (wall clock) position: content divided by tempo, so a stretched source never runs
    /// the shared clock at its own content rate.
    /// </summary>
    double RustNativeRealPosition { get; }

    /// <summary>
    /// Network driven drift correction, called from the mixer's control tick.
    /// </summary>
    void ApplyRustNativeSync();
}
