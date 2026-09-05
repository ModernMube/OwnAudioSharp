using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native MIDI clock pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiClockHandle : MidiPtrHandle
{
    /// <inheritdoc />
    protected override void Destroy(IntPtr ptr) => MidiNativeMethods.ownaudio_midi_v1_clock_destroy(ptr);
}
