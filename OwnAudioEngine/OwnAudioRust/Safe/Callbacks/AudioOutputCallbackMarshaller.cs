using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Glues a managed fill handler onto the unmanaged callback the FFI wants.
/// </summary>
/// <remarks>
/// The entry point is a static [UnmanagedCallersOnly] method rather than a delegate: iOS runs
/// AOT-only, and a delegate needs a native-to-managed thunk built at run time, which is exactly
/// what it cannot do. We find the way back to the instance through the user_data pointer the
/// engine hands to every callback. Dispose only after the stream is destroyed.
/// </remarks>
internal sealed unsafe class AudioOutputCallbackMarshaller : IDisposable
{
    private readonly AudioOutputCallbackHandler _userCallback;
    private GCHandle _self;
    private int _disposed;

    /// <summary>
    /// Fires on a threadpool thread if the user callback blew up. We swallow it at the
    /// boundary, an exception must never walk into native code.
    /// </summary>
    internal event EventHandler<Exception>? CallbackError;

    internal AudioOutputCallbackMarshaller(AudioOutputCallbackHandler userCallback)
    {
        _userCallback = userCallback;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// Function pointer for ownaudio_v1_open_output_stream.
    /// </summary>
    internal static IntPtr NativeFunctionPointer
        => (IntPtr)(delegate* unmanaged[Cdecl]<float*, nuint, ushort, void*, void>)&_nativeEntry;

    /// <summary>
    /// Goes in as user_data and comes back on every callback — this is how the static entry
    /// finds its instance again.
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
        AudioOutputCallbackMarshaller? _target = _fromUserData(userData);
        if (_target is null) return;

        try
        {
            var args = new AudioOutputCallbackArgs(buffer, (int)frameCount, channels);
            _target._userCallback(in args);
        }
        catch (Exception ex)
        {
            new Span<float>(buffer, (int)frameCount * channels).Clear();
            _target._raiseError(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AudioOutputCallbackMarshaller? _fromUserData(void* userData)
    {
        if (userData is null) return null;
        return GCHandle.FromIntPtr((IntPtr)userData).Target as AudioOutputCallbackMarshaller;
    }

    private void _raiseError(Exception ex)
    {
        EventHandler<Exception>? handler = CallbackError;
        if (handler is null) return;

        ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, ex));
    }
}
