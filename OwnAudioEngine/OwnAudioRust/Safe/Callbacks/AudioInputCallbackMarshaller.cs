using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Bridges a managed <see cref="AudioInputCallbackHandler"/> to the unmanaged
/// <see cref="NativeInputCallback"/> delegate required by the FFI layer,
/// without allocating on each invocation.
/// </summary>
/// <remarks>
/// Same lifetime and pinning guarantees as <see cref="AudioOutputCallbackMarshaller"/>.
/// </remarks>
internal sealed class AudioInputCallbackMarshaller : IDisposable
{
    #region Fields

    private readonly AudioInputCallbackHandler _userCallback;
    private readonly NativeInputCallback _nativeDelegate;
    private readonly GCHandle _pin;
    private int _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Raised (on a <see cref="ThreadPool"/> thread) when the user callback throws an exception.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    #endregion

    #region Construction and disposal

    /// <summary>
    /// Initializes the marshaller and pins the native delegate.
    /// </summary>
    /// <param name="userCallback">The user-supplied capture callback.</param>
    internal unsafe AudioInputCallbackMarshaller(AudioInputCallbackHandler userCallback)
    {
        _userCallback   = userCallback;
        _nativeDelegate = NativeCallbackEntryPoint;
        _pin            = GCHandle.Alloc(_nativeDelegate);
    }

    /// <summary>
    /// Returns the native function pointer to pass to <c>ownaudio_v1_open_input_stream</c>.
    /// Valid until <see cref="Dispose"/> is called.
    /// </summary>
    internal unsafe IntPtr NativeFunctionPointer
        => Marshal.GetFunctionPointerForDelegate(_nativeDelegate);

    /// <summary>
    /// Releases the pinned GCHandle. Call only after the native stream is destroyed.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pin.Free();
        }
    }

    #endregion

    #region Native callback entry point

    private unsafe void NativeCallbackEntryPoint(
        float* buffer,
        nuint frameCount,
        ushort channels,
        void* userData)
    {
        try
        {
            var args = new AudioInputCallbackArgs(buffer, (int)frameCount, channels);
            _userCallback(in args);
        }
        catch (Exception ex)
        {
            RaiseCallbackErrorAsync(ex);
        }
    }

    private void RaiseCallbackErrorAsync(Exception ex)
    {
        EventHandler<Exception>? handler = CallbackError;
        if (handler is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, ex));
    }

    #endregion
}
