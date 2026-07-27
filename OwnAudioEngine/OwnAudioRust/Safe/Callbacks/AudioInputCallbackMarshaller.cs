using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Glues a managed capture handler onto the unmanaged callback the FFI wants.
/// Same static-entry and lifetime deal as the output marshaller.
/// </summary>
internal sealed unsafe class AudioInputCallbackMarshaller : IDisposable
{
    private readonly AudioInputCallbackHandler _userCallback;
    private GCHandle _self;
    private int _disposed;

    /// <summary>
    /// Fires on a threadpool thread if the user callback blew up.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    internal AudioInputCallbackMarshaller(AudioInputCallbackHandler userCallback)
    {
        _userCallback = userCallback;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// Function pointer for ownaudio_v1_open_input_stream.
    /// </summary>
    internal static IntPtr NativeFunctionPointer
        => (IntPtr)(delegate* unmanaged[Cdecl]<float*, nuint, ushort, void*, void>)&_nativeEntry;

    /// <summary>
    /// The user_data we hand to the engine so the static entry can find us again.
    /// </summary>
    internal IntPtr UserData => GCHandle.ToIntPtr(_self);

    /// <summary>
    /// Frees the handle. Only after the native stream is gone!
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _self.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void _nativeEntry(float* buffer, nuint frameCount, ushort channels, void* userData)
    {
        AudioInputCallbackMarshaller? _target = _fromUserData(userData);
        if (_target is null) return;

        try
        {
            var args = new AudioInputCallbackArgs(buffer, (int)frameCount, channels);
            _target._userCallback(in args);
        }
        catch (Exception ex)
        {
            _target._raiseError(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AudioInputCallbackMarshaller? _fromUserData(void* userData)
    {
        if (userData is null) return null;
        return GCHandle.FromIntPtr((IntPtr)userData).Target as AudioInputCallbackMarshaller;
    }

    private void _raiseError(Exception ex)
    {
        EventHandler<Exception>? handler = CallbackError;
        if (handler is null) return;

        ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, ex));
    }
}
