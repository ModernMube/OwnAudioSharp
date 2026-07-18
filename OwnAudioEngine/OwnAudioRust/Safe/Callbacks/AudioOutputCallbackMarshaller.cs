using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Glues a managed fill handler onto the unmanaged NativeOutputCallback the FFI wants.
/// The delegate stays pinned for the whole lifetime, otherwise the GC could move it out
/// from under the native audio thread. Dispose only after the stream is destroyed.
/// </summary>
internal sealed class AudioOutputCallbackMarshaller : IDisposable
{
    private readonly AudioOutputCallbackHandler _userCallback;
    private readonly NativeOutputCallback _nativeDelegate;
    private readonly GCHandle _pin;
    private int _disposed;

    /// <summary>
    /// Fires on a threadpool thread if the user callback blew up. We swallow it at the
    /// boundary, an exception must never walk into native code.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    internal unsafe AudioOutputCallbackMarshaller(AudioOutputCallbackHandler userCallback)
    {
        _userCallback = userCallback;
        _nativeDelegate = _nativeEntry;
        _pin = GCHandle.Alloc(_nativeDelegate);
    }

    /// <summary>
    /// Function pointer for ownaudio_v1_open_output_stream. Dead once disposed.
    /// </summary>
    internal IntPtr NativeFunctionPointer => Marshal.GetFunctionPointerForDelegate(_nativeDelegate);

    /// <summary>
    /// Frees the pin. Only after the native stream is gone!
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _pin.Free(); }
    }

    private unsafe void _nativeEntry(float* buffer, nuint frameCount, ushort channels, void* userData)
    {
        try
        {
            var args = new AudioOutputCallbackArgs(buffer, (int)frameCount, channels);
            _userCallback(in args);
        }
        catch (Exception ex)
        {
            new Span<float>(buffer, (int)frameCount * channels).Clear();
            _raiseError(ex);
        }
    }

    private void _raiseError(Exception ex)
    {
        EventHandler<Exception>? handler = CallbackError;
        if (handler is null) return;

        ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, ex));
    }
}
