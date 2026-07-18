using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native SMF writer pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiWriterHandle : SafeHandle
{
    /// <summary>
    /// Starts out invalid, the FFI out param fills it in.
    /// </summary>
    public MidiWriterHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_writer_destroy(handle);
        return true;
    }
}
