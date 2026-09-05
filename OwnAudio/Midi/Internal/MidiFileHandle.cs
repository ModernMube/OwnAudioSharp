using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Parsed native MIDI file. It owns the event payload memory as well, so don't
/// hold on to any MetaData pointer past the dispose.
/// </summary>
internal sealed class MidiFileHandle : MidiPtrHandle
{
    /// <inheritdoc />
    protected override void Destroy(IntPtr ptr) => MidiNativeMethods.ownaudio_midi_v1_file_destroy(ptr);
}
