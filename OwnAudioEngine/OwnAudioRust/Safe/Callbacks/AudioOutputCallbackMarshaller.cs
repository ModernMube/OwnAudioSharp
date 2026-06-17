using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Bridges a managed <see cref="AudioOutputCallbackHandler"/> to the unmanaged
/// <see cref="NativeOutputCallback"/> delegate required by the FFI layer,
/// without allocating on each invocation.
/// </summary>
/// <remarks>
/// <para>
/// The managed delegate is pinned via <see cref="GCHandle"/> for the entire lifetime of the
/// marshaller, preventing the GC from relocating or collecting it while the native audio
/// thread holds a function pointer to it.
/// </para>
/// <para>
/// Dispose this instance only after the corresponding native stream has been destroyed.
/// </para>
/// </remarks>
internal sealed class AudioOutputCallbackMarshaller : IDisposable
{
    #region Fields

    private readonly AudioOutputCallbackHandler _userCallback;
    private readonly NativeOutputCallback _nativeDelegate;
    private readonly GCHandle _pin;
    private int _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Raised (on a <see cref="ThreadPool"/> thread) when the user callback throws an exception.
    /// The exception is swallowed at the FFI boundary to prevent crossing into native code.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    #endregion

    #region Construction and disposal

    /// <summary>
    /// Initializes the marshaller and pins the native delegate.
    /// </summary>
    /// <param name="userCallback">The user-supplied audio fill callback.</param>
    internal unsafe AudioOutputCallbackMarshaller(AudioOutputCallbackHandler userCallback)
    {
        _userCallback   = userCallback;
        _nativeDelegate = NativeCallbackEntryPoint;
        _pin            = GCHandle.Alloc(_nativeDelegate);
    }

    /// <summary>
    /// Returns the native function pointer to pass to <c>ownaudio_v1_open_output_stream</c>.
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
            var args = new AudioOutputCallbackArgs(buffer, (int)frameCount, channels);
            _userCallback(in args);
        }
        catch (Exception ex)
        {
            // Silence the output buffer so the native side receives zeros rather than garbage.
            new Span<float>(buffer, (int)frameCount * channels).Clear();
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
