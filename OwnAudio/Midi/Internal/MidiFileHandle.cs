using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Parsed native MIDI file. It owns the event payload memory as well, so don't
/// hold on to any MetaData pointer past the dispose.
/// </summary>
internal sealed class MidiFileHandle : SafeHandle
{
    /// <summary>
    /// Starts out invalid, the FFI out param fills it in.
    /// </summary>
    public MidiFileHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_file_destroy(handle);
        return true;
    }
}
