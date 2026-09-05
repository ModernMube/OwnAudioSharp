using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Shared plumbing for the MIDI pointer handles: null means invalid, the derived type only
/// says how it gets freed. Separate types so an FFI out param can't cross-assign.
/// </summary>
internal abstract class MidiPtrHandle : SafeHandle
{
    /// <summary>
    /// Starts out invalid, the FFI out param fills it in.
    /// </summary>
    protected MidiPtrHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Hands the pointer back to the native side.
    /// </summary>
    protected abstract void Destroy(IntPtr ptr);

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        Destroy(handle);
        return true;
    }
}
