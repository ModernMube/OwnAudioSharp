using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native MIDI output port pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiOutputPortHandle : MidiPtrHandle
{
    /// <inheritdoc />
    protected override void Destroy(IntPtr ptr) => MidiNativeMethods.ownaudio_midi_v1_output_port_destroy(ptr);
}
