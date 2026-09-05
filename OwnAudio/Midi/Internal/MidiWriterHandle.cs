using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Native SMF writer pointer, destroyed on dispose.
/// </summary>
internal sealed class MidiWriterHandle : MidiPtrHandle
{
    /// <inheritdoc />
    protected override void Destroy(IntPtr ptr) => MidiNativeMethods.ownaudio_midi_v1_writer_destroy(ptr);
}
