using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Safe wrapper around a native MIDI file writer pointer. Releases the native
/// writer through <c>ownaudio_midi_v1_writer_destroy</c> when disposed.
/// </summary>
internal sealed class MidiWriterHandle : SafeHandle
{
    /// <summary>
    /// Creates an invalid handle; the native layer fills it via an out parameter.
    /// </summary>
    public MidiWriterHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        MidiNativeMethods.ownaudio_midi_v1_writer_destroy(handle);
        return true;
    }
}
