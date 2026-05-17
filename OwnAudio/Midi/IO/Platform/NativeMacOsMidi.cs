using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

/// <summary>
/// Thin P/Invoke wrapper exposing the CoreMIDI source and destination index accessors
/// used by <see cref="MidiPortFactory"/> to resolve endpoint handles by index.
/// </summary>
internal static partial class NativeMacOsMidi
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

    /// <summary>
    /// Returns the CoreMIDI source endpoint at the given zero-based index.
    /// </summary>
    [LibraryImport(CoreMidi)]
    internal static partial nint MIDIGetSource(nint index);

    /// <summary>
    /// Returns the CoreMIDI destination endpoint at the given zero-based index.
    /// </summary>
    [LibraryImport(CoreMidi)]
    internal static partial nint MIDIGetDestination(nint index);
}
