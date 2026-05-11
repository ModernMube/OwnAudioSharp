using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

#if MACOS

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

#else

/// <summary>
/// Stub implementation of <see cref="NativeMacOsMidi"/> for non-macOS platforms,
/// allowing <see cref="MidiPortFactory"/> to compile without conditional blocks.
/// </summary>
internal static class NativeMacOsMidi
{
    /// <summary>
    /// Always returns zero — CoreMIDI is not available on this platform.
    /// </summary>
    internal static nint MIDIGetSource(nint index) => 0;

    /// <summary>
    /// Always returns zero — CoreMIDI is not available on this platform.
    /// </summary>
    internal static nint MIDIGetDestination(nint index) => 0;
}

#endif
