using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Shared plumbing for the raw pointer handles: null means invalid, and the derived type
/// only has to say how the thing gets freed. The types stay separate so P/Invoke can't
/// hand a mixer pointer to something expecting a track.
/// </summary>
public abstract class NativePtrHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    protected NativePtrHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Hands the pointer back to the native side. Called once, on a handle we know is valid.
    /// </summary>
    protected abstract void Destroy(IntPtr ptr);

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        Destroy(handle);
        return true;
    }
}
