using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Safe wrapper around a native MIDI input port pointer. Releases the native
/// port through <c>ownaudio_midi_v1_input_port_destroy</c> when disposed.
/// </summary>
internal sealed class MidiInputPortHandle : SafeHandle
{
    /// <summary>
    /// Creates an invalid handle; the native layer fills it via an out parameter.
    /// </summary>
    public MidiInputPortHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_input_port_destroy(handle);
        return true;
    }
}
