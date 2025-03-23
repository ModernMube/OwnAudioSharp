using System.Diagnostics;
using System.Runtime.InteropServices;
using Ownaudio.Bindings.PortAudio;
using Ownaudio.Exceptions;

namespace Ownaudio.Utilities.Extensions;

/// <summary>
/// Additional functions of portaudio
/// </summary>
internal static class PortAudioExtensions
{
    /// <summary>
    /// Returns true if the code is an error code
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    public static bool PaIsError(this int code)
    {
        return code < 0;
    }

    /// <summary>
    /// If the received code is a portaudio error code, it throws an error.
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    /// <exception cref="PortAudioException"></exception>
    public static int PaGuard(this int code)
    {
        if (!code.PaIsError())
        {
            return code;
        }

        if(code != -9981)
            throw new PortAudioException(code);
        else
        {
            Debug.WriteLine("Portaudio input overflowed");
            return code;
        }
            
    }

    /// <summary>
    /// Returns the error message for the given code.
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    public static string? PaErrorToText(this int code)
    {
        return Marshal.PtrToStringAnsi(PaBinding.Pa_GetErrorText(code));
    }

    /// <summary>
    /// Returns the input and output parameters of the specified audio
    /// </summary>
    /// <param name="device"></param>
    /// <returns></returns>
    public static PaBinding.PaDeviceInfo PaGetPaDeviceInfo(this int device)
    {
        return Marshal.PtrToStructure<PaBinding.PaDeviceInfo>(PaBinding.Pa_GetDeviceInfo(device));
    }

    /// <summary>
    /// It returns with the parameters of the specified Audio Host
    /// </summary>
    /// <param name="api"></param>
    /// <returns></returns>
    public static PaBinding.PaHostApiInfo PaHostApiInfo(this int api)
    {
        return Marshal.PtrToStructure<PaBinding.PaHostApiInfo>(PaBinding.Pa_GetHostApiInfo(api));
    }

    /// <summary>
    /// Creates a new audio port based on the audio input and output parameters.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="deviceIndex"></param>
    /// <returns></returns>
    public static AudioDevice PaToAudioDevice(this PaBinding.PaDeviceInfo device, int deviceIndex)
    {
        return new AudioDevice(
            deviceIndex,
            device.name,
            device.maxOutputChannels,
            device.maxInputChannels,
            device.defaultLowOutputLatency,
            device.defaultHighOutputLatency,
            device.defaultLowInputLatency,
            device.defaultHighInputLatency,
            (int)device.defaultSampleRate);
    }
}
