using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Safe wrapper around a parsed native MIDI file pointer. Releases the native
/// file (and the event payload memory it owns) through
/// <c>ownaudio_midi_v1_file_destroy</c> when disposed.
/// </summary>
internal sealed class MidiFileHandle : SafeHandle
{
    /// <summary>
    /// Creates an invalid handle; the native layer fills it via an out parameter.
    /// </summary>
    public MidiFileHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_file_destroy(handle);
        return true;
    }
}
