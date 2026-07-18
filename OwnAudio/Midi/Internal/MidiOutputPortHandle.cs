using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native MIDI output port pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiOutputPortHandle : SafeHandle
{
    /// <summary>
    /// Starts out invalid, the FFI out param fills it in.
    /// </summary>
    public MidiOutputPortHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_output_port_destroy(handle);
        return true;
    }
}
