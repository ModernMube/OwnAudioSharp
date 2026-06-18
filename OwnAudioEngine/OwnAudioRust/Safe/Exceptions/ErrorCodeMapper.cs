using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Central mapper from raw native error codes to typed managed exceptions.
/// Every native call result must pass through <see cref="ThrowIfError"/> — direct
/// <c>if (code != 0) throw</c> patterns at the call site are forbidden so that
/// error messages and exception types remain consistent across the entire codebase.
/// </summary>
internal static class ErrorCodeMapper
{
    #region Public API

    /// <summary>
    /// Throws the appropriate managed exception for <paramref name="rawCode"/> if it indicates
    /// failure. Does nothing when <paramref name="rawCode"/> is zero (Success).
    /// </summary>
    /// <param name="rawCode">The integer error code returned by the native function.</param>
    /// <param name="operation">
    /// Name of the operation that failed.
    /// Pass <c>nameof(TheCallingMethod)</c> at every call site.
    /// </param>
    /// <exception cref="DeviceException">Thrown for device-related error codes.</exception>
    /// <exception cref="StreamException">Thrown for stream-related error codes.</exception>
    /// <exception cref="OwnAudioException">Thrown for all other non-zero codes.</exception>
    internal static void ThrowIfError(int rawCode, string operation)
    {
        if (rawCode == (int)NativeErrorCode.Success)
        {
            return;
        }

        var nativeCode = (NativeErrorCode)rawCode;
        AudioEngineErrorCode publicCode = ToPublicCode(nativeCode);

        string detail = GetNativeErrorDetail();
        string message = string.IsNullOrEmpty(detail)
            ? $"{operation} failed: {DescribeCode(nativeCode)} (code {rawCode})"
            : $"{operation} failed: {DescribeCode(nativeCode)} (code {rawCode}) — {detail}";

        throw nativeCode switch
        {
            NativeErrorCode.DeviceNotFound
                or NativeErrorCode.DeviceEnumerationFailed
                    => new DeviceException(publicCode, message),

            NativeErrorCode.UnsupportedConfig
                or NativeErrorCode.StreamBuildFailed
                or NativeErrorCode.StreamControlFailed
                    => new StreamException(publicCode, message),

            NativeErrorCode.HostApiNotAvailable
                    => new HostApiNotAvailableException(message),

            NativeErrorCode.AsioDriverNotFound
                    => new AsioDriverNotFoundException(message),

            _ => new OwnAudioException(publicCode, message)
        };
    }

    #endregion

    #region Private helpers

    private static AudioEngineErrorCode ToPublicCode(NativeErrorCode code)
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
            _                                       => AudioEngineErrorCode.InternalError,
        };
    }

    private static string GetNativeErrorDetail()
    {
        try
        {
            IntPtr ptr = OwnAudioNative.ownaudio_v1_last_error_message();
            return ptr == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeCode(NativeErrorCode code)
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
            _                                       => $"unknown error code {(int)code}",
        };
    }

    #endregion
}
