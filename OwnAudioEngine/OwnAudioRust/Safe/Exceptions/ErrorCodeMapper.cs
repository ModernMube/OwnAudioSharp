using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Single place where raw native codes turn into typed exceptions. Every FFI result goes
/// through here — no ad-hoc "if (code != 0) throw" at the call sites, otherwise the
/// messages and types drift apart.
/// </summary>
internal static class ErrorCodeMapper
{
    /// <summary>
    /// Throws for anything non-zero, no-op on Success. Pass nameof(caller) as operation,
    /// that's what shows up in front of the message.
    /// </summary>
    internal static void ThrowIfError(int rawCode, string operation)
    {
        if (rawCode == (int)NativeErrorCode.Success) { return; }

        var native = (NativeErrorCode)rawCode;
        var code = _publicCode(native);

        string detail = _lastNativeError();
        string message = detail.Length == 0
            ? $"{operation} failed: {_describe(native)} (code {rawCode})"
            : $"{operation} failed: {_describe(native)} (code {rawCode}) — {detail}";

        throw native switch
        {
            NativeErrorCode.DeviceNotFound
                or NativeErrorCode.DeviceEnumerationFailed
                    => new DeviceException(code, message),

            NativeErrorCode.UnsupportedConfig
                or NativeErrorCode.StreamBuildFailed
                or NativeErrorCode.StreamControlFailed
                    => new StreamException(code, message),

            NativeErrorCode.HostApiNotAvailable => new HostApiNotAvailableException(message),
            NativeErrorCode.AsioDriverNotFound  => new AsioDriverNotFoundException(message),

            NativeErrorCode.DecoderOpenFailed
                or NativeErrorCode.DecoderUnsupportedFormat
                or NativeErrorCode.DecoderReadFailed
                or NativeErrorCode.DecoderSeekFailed
                    => new DecoderException(code, message),

            _ => new OwnAudioException(code, message)
        };
    }

    private static AudioEngineErrorCode _publicCode(NativeErrorCode code)
    {
        return code switch
        {
            NativeErrorCode.Success                 => AudioEngineErrorCode.Success,
            NativeErrorCode.DeviceNotFound          => AudioEngineErrorCode.DeviceNotFound,
            NativeErrorCode.DeviceEnumerationFailed => AudioEngineErrorCode.DeviceEnumerationFailed,
            NativeErrorCode.UnsupportedConfig       => AudioEngineErrorCode.UnsupportedConfig,
            NativeErrorCode.StreamBuildFailed       => AudioEngineErrorCode.StreamBuildFailed,
            NativeErrorCode.StreamControlFailed     => AudioEngineErrorCode.StreamControlFailed,
            NativeErrorCode.NullPointer             => AudioEngineErrorCode.NullPointer,
            NativeErrorCode.InvalidHandle           => AudioEngineErrorCode.InvalidHandle,
            NativeErrorCode.InternalPanic           => AudioEngineErrorCode.InternalPanic,
            NativeErrorCode.InternalError           => AudioEngineErrorCode.InternalError,
            NativeErrorCode.HostApiNotAvailable     => AudioEngineErrorCode.HostApiNotAvailable,
            NativeErrorCode.AsioDriverNotFound      => AudioEngineErrorCode.AsioDriverNotFound,
            NativeErrorCode.DecoderOpenFailed       => AudioEngineErrorCode.DecoderOpenFailed,
            NativeErrorCode.DecoderUnsupportedFormat => AudioEngineErrorCode.DecoderUnsupportedFormat,
            NativeErrorCode.DecoderReadFailed       => AudioEngineErrorCode.DecoderReadFailed,
            NativeErrorCode.DecoderSeekFailed       => AudioEngineErrorCode.DecoderSeekFailed,
            _                                       => AudioEngineErrorCode.InternalError,
        };
    }

    /// <summary>
    /// Extra context string the Rust side stashed away, empty if there's none.
    /// </summary>
    private static string _lastNativeError()
    {
        IntPtr ptr = OwnAudioNative.ownaudio_v1_last_error_message();
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    private static string _describe(NativeErrorCode code)
    {
        return code switch
        {
            NativeErrorCode.Success                 => "success",
            NativeErrorCode.DeviceNotFound          => "audio device not found",
            NativeErrorCode.DeviceEnumerationFailed => "device enumeration failed",
            NativeErrorCode.UnsupportedConfig       => "stream configuration not supported by device",
            NativeErrorCode.StreamBuildFailed       => "failed to build audio stream",
            NativeErrorCode.StreamControlFailed     => "stream play/pause control failed",
            NativeErrorCode.NullPointer             => "unexpected null pointer argument",
            NativeErrorCode.InvalidHandle           => "invalid or already-destroyed handle",
            NativeErrorCode.InternalPanic           => "internal panic in native audio engine",
            NativeErrorCode.InternalError           => "internal error in native audio engine",
            NativeErrorCode.HostApiNotAvailable     => "requested audio host API is not available on this system",
            NativeErrorCode.AsioDriverNotFound      => "ASIO host API is available but no ASIO driver is installed",
            NativeErrorCode.DecoderOpenFailed       => "failed to open or probe the audio file",
            NativeErrorCode.DecoderUnsupportedFormat => "audio file format or codec is not supported",
            NativeErrorCode.DecoderReadFailed       => "error while decoding audio data",
            NativeErrorCode.DecoderSeekFailed       => "failed to seek within the audio stream",
            _                                       => $"unknown error code {(int)code}",
        };
    }
}
