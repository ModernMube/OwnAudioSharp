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
    /// Puts the clock's network tempo trim onto the native track, from the mixer's control tick.
    /// </summary>
    void ApplyRustNativeSync();

    /// <summary>
    /// Arms a jump in project seconds on the native side, worked out to this source's own content
    /// frames. False when the source cannot carry one out itself (yet) or the frames fall before it.
    /// </summary>
    bool ArmRustNativeJump(ulong id, double atProject, double toProject);

    void DisarmRustNativeJump();

    /// <summary>
    /// Id of the last jump carried out, 0 for none.
    /// </summary>
    ulong RustNativeJumpTaken { get; }

    /// <summary>
    /// Id of the last jump whose moment came before the source was ready for it, so it did not
    /// happen; 0 for none.
    /// </summary>
    ulong RustNativeJumpMissed { get; }
}
