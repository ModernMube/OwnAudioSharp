using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

#if MACOS

internal static partial class NativeMacOsMidi
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

    [LibraryImport(CoreMidi)]
    internal static partial nint MIDIGetSource(nint index);

    [LibraryImport(CoreMidi)]
    internal static partial nint MIDIGetDestination(nint index);
}

#else

// Stub so MidiPortFactory.cs compiles on non-macOS without #if blocks in the factory
internal static class NativeMacOsMidi
{
    internal static nint MIDIGetSource(nint index) => 0;
    internal static nint MIDIGetDestination(nint index) => 0;
}

#endif
