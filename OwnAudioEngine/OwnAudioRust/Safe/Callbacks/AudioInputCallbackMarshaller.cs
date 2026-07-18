using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Glues a managed capture handler onto the unmanaged NativeInputCallback the FFI wants.
/// Same pinning/lifetime deal as the output marshaller.
/// </summary>
internal sealed class AudioInputCallbackMarshaller : IDisposable
{
    private readonly AudioInputCallbackHandler _userCallback;
    private readonly NativeInputCallback _nativeDelegate;
    private readonly GCHandle _pin;
    private int _disposed;

    /// <summary>
    /// Fires on a threadpool thread if the user callback blew up.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    internal unsafe AudioInputCallbackMarshaller(AudioInputCallbackHandler userCallback)
    {
        _userCallback = userCallback;
        _nativeDelegate = _nativeEntry;
        _pin = GCHandle.Alloc(_nativeDelegate);
    }

    /// <summary>
    /// Function pointer for ownaudio_v1_open_input_stream. Dead once disposed.
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
            var args = new AudioInputCallbackArgs(buffer, (int)frameCount, channels);
            _userCallback(in args);
        }
        catch (Exception ex)
        {
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
