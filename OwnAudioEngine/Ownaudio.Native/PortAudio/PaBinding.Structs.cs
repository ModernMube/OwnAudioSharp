using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.PortAudio;

internal static partial class PaBinding
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PaVersionInfo
    {
        public readonly int versionMajor;
        public readonly int versionMinor;
        public readonly int versionSubMinor;

        [MarshalAs(UnmanagedType.LPStr)]
        public readonly string versionControlRevision;

        [MarshalAs(UnmanagedType.LPStr)]
        public readonly string verionText;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaStreamParameters
    {
        public int device;
        public int channelCount;
        public PaSampleFormat sampleFormat;
        public double suggestedLatency;
        public IntPtr hostApiSpecificStreamInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PaDeviceInfo
    {
        public readonly int structVersion;

        [MarshalAs(UnmanagedType.LPStr)]
        public readonly string name;

        public readonly int hostApi;
        public readonly int maxInputChannels;
        public readonly int maxOutputChannels;
        public readonly double defaultLowInputLatency;
        public readonly double defaultLowOutputLatency;
        public readonly double defaultHighInputLatency;
        public readonly double defaultHighOutputLatency;
        public readonly double defaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PaHostApiInfo
    {
        public readonly int structVersion;
        public readonly PaHostApiTypeId type;

        [MarshalAs(UnmanagedType.LPStr)]
        public readonly string name;

        public readonly int deviceCount;
        public readonly int defaultInputDevice;
        public readonly int defaultOutputDevice;
    }

    /// <summary>
    /// Represents WASAPI-specific stream information for PortAudio.
    /// This structure allows advanced configuration for WASAPI streams.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PaWasapiStreamInfo
    {
        /// <summary>
        /// Size of this structure in bytes. Must be set to sizeof(PaWasapiStreamInfo).
        /// </summary>
        public uint size;

        /// <summary>
        /// Host API type. Must be set to PaHostApiTypeId.paWASAPI.
        /// </summary>
        public int hostApiType;

        /// <summary>
        /// Version of the structure. Must be set to 1.
        /// </summary>
        public uint version;

        /// <summary>
        /// Flags that control WASAPI-specific behavior.
        /// These are a combination of values from the PaWasapiFlags enumeration.
        /// </summary>
        public uint flags;

        /// <summary>
        /// Specifies the channel mask to address specific speakers in a multichannel stream.
        /// This is used only if paWinWasapiUseChannelMask flag is set.
        /// </summary>
        public uint channelMask;

        /// <summary>
        /// Callback for processing output audio data, bypassing PortAudio's internal processing.
        /// Used only if the paWinWasapiRedirectHostProcessor flag is set.
        /// </summary>
        public IntPtr hostProcessorOutput;

        /// <summary>
        /// Callback for processing input audio data, bypassing PortAudio's internal processing.
        /// Used only if the paWinWasapiRedirectHostProcessor flag is set.
        /// </summary>
        public IntPtr hostProcessorInput;

        /// <summary>
        /// Specifies the thread priority explicitly.
        /// Used only if the paWinWasapiThreadPriority flag is set.
        /// </summary>
        public PaWasapiThreadPriority threadPriority;

        /// <summary>
        /// Specifies the stream category.
        /// </summary>
        public PaWasapiStreamCategory streamCategory;

        /// <summary>
        /// Specifies additional stream options.
        /// </summary>
        public PaWasapiStreamOption streamOption;

        /// <summary>
        /// Details for passthrough mode.
        /// Used only if the paWinWasapiPassthrough flag is set in flags.
        /// </summary>
        public PaWasapiStreamPassthrough passthrough;
    }

    /// <summary>
    /// Represents details for passthrough mode in WASAPI.
    /// This is used to configure encoded audio streams (e.g., Dolby, DTS).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PaWasapiStreamPassthrough
    {
        /// <summary>
        /// The format of the encoded audio stream.
        /// </summary>
        public PaWasapiPassthroughFormat formatId;

        /// <summary>
        /// The sample rate of the encoded audio stream in samples per second.
        /// </summary>
        public uint encodedSamplesPerSec;

        /// <summary>
        /// The number of encoded audio channels.
        /// </summary>
        public uint encodedChannelCount;

        /// <summary>
        /// The average number of bytes per second for the encoded audio stream.
        /// </summary>
        public uint averageBytesPerSec;
    }

    /// <summary>
    /// Represents ASIO-specific stream information for PortAudio.
    /// This structure allows advanced configuration for ASIO streams.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PaAsioStreamInfo
    {
        public uint size;             // unsigned long -> uint
        public PaHostApiTypeId hostApiType; // Enum marad
        public uint version;          // unsigned long -> uint
        public uint flags;            // unsigned long -> uint

        /// <summary>
        /// Ha a `paAsioUseChannelSelectors` flag be van állítva, 
        /// akkor ez egy mutató egy int tömbre, amely a használt ASIO csatornákat adja meg.
        /// </summary>
        public IntPtr channelSelectors; // int* -> IntPtr (mutató C#-ban)

        /// <summary>
        /// Visszaadja a csatornaválasztó tömböt, ha van.
        /// </summary>
        public int[] GetChannelSelectors(int channelCount)
        {
            if (channelSelectors == IntPtr.Zero || channelCount <= 0)
                return Array.Empty<int>();

            int[] selectors = new int[channelCount];
            Marshal.Copy(channelSelectors, selectors, 0, channelCount);
            return selectors;
        }

        /// <summary>
        /// Beállítja a csatornaválasztó tömböt.
        /// </summary>
        public void SetChannelSelectors(int[] selectors)
        {
            if (channelSelectors != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(channelSelectors); // Meglévő memória felszabadítása
            }

            if (selectors == null || selectors.Length == 0)
            {
                channelSelectors = IntPtr.Zero;
                return;
            }

            channelSelectors = Marshal.AllocHGlobal(selectors.Length * sizeof(int));
            Marshal.Copy(selectors, 0, channelSelectors, selectors.Length);
        }

        /// <summary>
        /// Felszabadítja az erőforrásokat.
        /// </summary>
        public void Free()
        {
            if (channelSelectors != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(channelSelectors);
                channelSelectors = IntPtr.Zero;
            }
        }
    }
}
