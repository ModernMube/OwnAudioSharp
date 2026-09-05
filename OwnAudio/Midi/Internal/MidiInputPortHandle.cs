using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native MIDI input port pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiInputPortHandle : MidiPtrHandle
{
    /// <inheritdoc />
    protected override void Destroy(IntPtr ptr) => MidiNativeMethods.ownaudio_midi_v1_input_port_destroy(ptr);
}
