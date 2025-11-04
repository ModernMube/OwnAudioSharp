using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop
{
    /// <summary>
    /// WASAPI (Windows Audio Session API) COM Interop definitions.
    /// All structures and interfaces are for P/Invoke and COM interop.
    /// </summary>
    internal static class WasapiInterop
    {
        // CLSIDs and IIDs for WASAPI
        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        /// <summary>
        /// Audio client share modes for WASAPI.
        /// </summary>
        public enum AudioClientShareMode
        {
            /// <summary>
            /// Shared mode - multiple applications share the audio device.
            /// </summary>
            AUDCLNT_SHAREMODE_SHARED,

            /// <summary>
            /// Exclusive mode - single application has exclusive access.
            /// </summary>
            AUDCLNT_SHAREMODE_EXCLUSIVE
        }

        /// <summary>
        /// Audio client stream flags for WASAPI initialization.
        /// </summary>
        [Flags]
        public enum AudioClientStreamFlags : uint
        {
            /// <summary>
            /// No special flags.
            /// </summary>
            None = 0,

            /// <summary>
            /// Enable cross-process audio streaming.
            /// </summary>
            AUDCLNT_STREAMFLAGS_CROSSPROCESS = 0x00010000,

            /// <summary>
            /// Enable loopback mode (capture output).
            /// </summary>
            AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000,

            /// <summary>
            /// Use event-driven mode instead of polling.
            /// </summary>
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,

            /// <summary>
            /// Do not persist session volume/mute settings.
            /// </summary>
            AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000,

            /// <summary>
            /// Enable sample rate adjustment.
            /// </summary>
            AUDCLNT_STREAMFLAGS_RATEADJUST = 0x00100000,

            /// <summary>
            /// Automatically convert PCM format.
            /// </summary>
            AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000,

            /// <summary>
            /// Use default quality for sample rate conversion.
            /// </summary>
            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000
        }

        /// <summary>
        /// Data flow direction for audio endpoints.
        /// </summary>
        public enum EDataFlow
        {
            /// <summary>
            /// Audio rendering (output/playback) device.
            /// </summary>
            eRender = 0,

            /// <summary>
            /// Audio capture (input/recording) device.
            /// </summary>
            eCapture = 1,

            /// <summary>
            /// All audio devices (both render and capture).
            /// </summary>
            eAll = 2
        }

        /// <summary>
        /// Device role for audio endpoints.
        /// </summary>
        public enum ERole
        {
            /// <summary>
            /// Console role (system sounds).
            /// </summary>
            eConsole = 0,

            /// <summary>
            /// Multimedia role (music, videos, games).
            /// </summary>
            eMultimedia = 1,

            /// <summary>
            /// Communications role (voice chat, telephony).
            /// </summary>
            eCommunications = 2
        }

        /// <summary>
        /// WAVEFORMATEX structure for audio format description.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEX
        {
            /// <summary>
            /// Waveform format tag (PCM, IEEE_FLOAT, EXTENSIBLE, etc.).
            /// </summary>
            public ushort wFormatTag;

            /// <summary>
            /// Number of audio channels (1=mono, 2=stereo, etc.).
            /// </summary>
            public ushort nChannels;

            /// <summary>
            /// Sample rate in Hz (e.g., 44100, 48000).
            /// </summary>
            public uint nSamplesPerSec;

            /// <summary>
            /// Average bytes per second (nSamplesPerSec * nBlockAlign).
            /// </summary>
            public uint nAvgBytesPerSec;

            /// <summary>
            /// Block alignment in bytes (nChannels * wBitsPerSample / 8).
            /// </summary>
            public ushort nBlockAlign;

            /// <summary>
            /// Bits per sample (8, 16, 24, 32, etc.).
            /// </summary>
            public ushort wBitsPerSample;

            /// <summary>
            /// Size of extra format information in bytes.
            /// </summary>
            public ushort cbSize;
        }

        /// <summary>
        /// WAVEFORMATEXTENSIBLE structure for extended audio format description.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEXTENSIBLE
        {
            /// <summary>
            /// Base WAVEFORMATEX structure.
            /// </summary>
            public WAVEFORMATEX Format;

            /// <summary>
            /// Valid bits per sample or samples per block.
            /// </summary>
            public ushort Samples;

            /// <summary>
            /// Channel mask indicating speaker positions.
            /// </summary>
            public uint dwChannelMask;

            /// <summary>
            /// GUID identifying the audio data format subtype.
            /// </summary>
            public Guid SubFormat;
        }

        // Wave format tags
        public const ushort WAVE_FORMAT_PCM = 0x0001;
        public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
        public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT =
            new Guid("00000003-0000-0010-8000-00aa00389b71");

        // HRESULT codes
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        public const int AUDCLNT_E_NOT_INITIALIZED = unchecked((int)0x88890001);
        public const int AUDCLNT_E_ALREADY_INITIALIZED = unchecked((int)0x88890002);
        public const int AUDCLNT_E_WRONG_ENDPOINT_TYPE = unchecked((int)0x88890003);
        public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        public const int AUDCLNT_E_NOT_STOPPED = unchecked((int)0x88890005);
        public const int AUDCLNT_E_BUFFER_TOO_LARGE = unchecked((int)0x88890006);
        public const int AUDCLNT_E_OUT_OF_ORDER = unchecked((int)0x88890007);
        public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
        public const int AUDCLNT_E_INVALID_SIZE = unchecked((int)0x88890009);
        public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
        public const int AUDCLNT_E_BUFFER_OPERATION_PENDING = unchecked((int)0x8889000B);
        public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);

        // Reference time units (100-nanosecond intervals)
        public const long REFTIMES_PER_SEC = 10000000;
        public const long REFTIMES_PER_MILLISEC = 10000;
    }

    /// <summary>
    /// COM interface for IMMDeviceEnumerator.
    /// Used to enumerate audio endpoint devices.
    /// </summary>
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        /// <summary>
        /// Enumerates audio endpoint devices.
        /// </summary>
        /// <param name="dataFlow">The data flow direction (render/capture).</param>
        /// <param name="dwStateMask">Device state mask.</param>
        /// <param name="ppDevices">Receives the device collection interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int EnumAudioEndpoints(
            WasapiInterop.EDataFlow dataFlow,
            uint dwStateMask,
            out IMMDeviceCollection ppDevices);

        /// <summary>
        /// Gets the default audio endpoint device.
        /// </summary>
        /// <param name="dataFlow">The data flow direction (render/capture).</param>
        /// <param name="role">The device role.</param>
        /// <param name="ppEndpoint">Receives the device interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetDefaultAudioEndpoint(
            WasapiInterop.EDataFlow dataFlow,
            WasapiInterop.ERole role,
            out IMMDevice ppEndpoint);
    }

    /// <summary>
    /// COM interface for IMMDevice.
    /// Represents a multimedia device resource.
    /// </summary>
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        /// <summary>
        /// Activates a COM interface on the device.
        /// </summary>
        /// <param name="iid">Interface identifier.</param>
        /// <param name="dwClsCtx">Class context.</param>
        /// <param name="pActivationParams">Activation parameters.</param>
        /// <param name="ppInterface">Receives the activated interface.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint dwClsCtx,
            IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        /// <summary>
        /// Opens the device's property store.
        /// </summary>
        /// <param name="stgmAccess">Storage access mode.</param>
        /// <param name="ppProperties">Receives the property store interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

        /// <summary>
        /// Gets the device's unique identifier string.
        /// </summary>
        /// <param name="ppstrId">Receives the device ID string.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        /// <summary>
        /// Gets the current state of the device.
        /// </summary>
        /// <param name="pdwState">Receives the device state.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetState(out uint pdwState);
    }

    /// <summary>
    /// COM interface for IAudioClient.
    /// Enables initialization and control of an audio stream.
    /// </summary>
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        /// <summary>
        /// Initializes the audio client with the specified parameters.
        /// </summary>
        /// <param name="shareMode">Shared or exclusive mode.</param>
        /// <param name="streamFlags">Stream configuration flags.</param>
        /// <param name="hnsBufferDuration">Buffer duration in 100-nanosecond units.</param>
        /// <param name="hnsPeriodicity">Device period (exclusive mode only).</param>
        /// <param name="pFormat">Audio format description.</param>
        /// <param name="audioSessionGuid">Audio session GUID.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Initialize(
            WasapiInterop.AudioClientShareMode shareMode,
            WasapiInterop.AudioClientStreamFlags streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            [In] ref WasapiInterop.WAVEFORMATEXTENSIBLE pFormat,
            [In] ref Guid audioSessionGuid);

        /// <summary>
        /// Gets the size of the audio buffer in frames.
        /// </summary>
        /// <param name="pNumBufferFrames">Receives the buffer size.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetBufferSize(out uint pNumBufferFrames);

        /// <summary>
        /// Gets the stream latency.
        /// </summary>
        /// <param name="phnsLatency">Receives the latency in 100-nanosecond units.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetStreamLatency(out long phnsLatency);

        /// <summary>
        /// Gets the number of frames of padding in the buffer.
        /// </summary>
        /// <param name="pNumPaddingFrames">Receives the padding frame count.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetCurrentPadding(out uint pNumPaddingFrames);

        /// <summary>
        /// Checks whether the audio device supports a particular format.
        /// </summary>
        /// <param name="shareMode">Shared or exclusive mode.</param>
        /// <param name="pFormat">Audio format to check.</param>
        /// <param name="ppClosestMatch">Receives closest supported format if available.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int IsFormatSupported(
            WasapiInterop.AudioClientShareMode shareMode,
            [In] ref WasapiInterop.WAVEFORMATEXTENSIBLE pFormat,
            out IntPtr ppClosestMatch);

        /// <summary>
        /// Gets the device's native mix format.
        /// </summary>
        /// <param name="ppDeviceFormat">Receives the format description pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);

        /// <summary>
        /// Gets the device period.
        /// </summary>
        /// <param name="phnsDefaultDevicePeriod">Receives default period.</param>
        /// <param name="phnsMinimumDevicePeriod">Receives minimum period.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

        /// <summary>
        /// Starts the audio stream.
        /// </summary>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Start();

        /// <summary>
        /// Stops the audio stream.
        /// </summary>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Stop();

        /// <summary>
        /// Resets the audio stream.
        /// </summary>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Reset();

        /// <summary>
        /// Sets the event handle for event-driven mode.
        /// </summary>
        /// <param name="eventHandle">Event handle.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        /// <summary>
        /// Gets a service interface from the audio client.
        /// </summary>
        /// <param name="riid">Service interface identifier.</param>
        /// <param name="ppv">Receives the service interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    /// <summary>
    /// COM interface for IAudioRenderClient.
    /// Enables writing audio data to the rendering endpoint buffer.
    /// </summary>
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        /// <summary>
        /// Gets a pointer to the audio buffer for writing.
        /// </summary>
        /// <param name="numFramesRequested">Number of frames to write.</param>
        /// <param name="ppData">Receives the buffer pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetBuffer(uint numFramesRequested, out IntPtr ppData);

        /// <summary>
        /// Releases the audio buffer after writing.
        /// </summary>
        /// <param name="numFramesWritten">Number of frames written.</param>
        /// <param name="dwFlags">Buffer flags.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    /// <summary>
    /// COM interface for IAudioCaptureClient.
    /// Enables reading audio data from the capture endpoint buffer.
    /// </summary>
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        /// <summary>
        /// Gets a pointer to the next available capture buffer.
        /// </summary>
        /// <param name="ppData">Receives the buffer pointer.</param>
        /// <param name="pNumFramesToRead">Receives the number of frames available.</param>
        /// <param name="pdwFlags">Receives buffer flags.</param>
        /// <param name="pu64DevicePosition">Receives device position.</param>
        /// <param name="pu64QPCPosition">Receives QPC timestamp.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetBuffer(
            out IntPtr ppData,
            out uint pNumFramesToRead,
            out uint pdwFlags,
            out ulong pu64DevicePosition,
            out ulong pu64QPCPosition);

        /// <summary>
        /// Releases the capture buffer after reading.
        /// </summary>
        /// <param name="numFramesRead">Number of frames read.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        /// <summary>
        /// Gets the size of the next available packet.
        /// </summary>
        /// <param name="pNumFramesInNextPacket">Receives the packet size in frames.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    /// <summary>
    /// Ole32.dll P/Invoke methods for COM initialization and object creation.
    /// </summary>
    internal static class Ole32
    {
        /// <summary>
        /// Class context for in-process server.
        /// </summary>
        public const uint CLSCTX_INPROC_SERVER = 0x1;

        /// <summary>
        /// Class context for all types of servers.
        /// </summary>
        public const uint CLSCTX_ALL = 0x17;

        /// <summary>
        /// Multi-threaded apartment model.
        /// </summary>
        public const uint COINIT_MULTITHREADED = 0x0;

        /// <summary>
        /// Apartment-threaded model.
        /// </summary>
        public const uint COINIT_APARTMENTTHREADED = 0x2;

        /// <summary>
        /// Initializes the COM library for the current thread.
        /// </summary>
        /// <param name="pvReserved">Reserved, must be NULL.</param>
        /// <param name="dwCoInit">Concurrency model.</param>
        /// <returns>HRESULT error code.</returns>
        [DllImport("ole32.dll", PreserveSig = true)]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        /// <summary>
        /// Uninitializes the COM library for the current thread.
        /// </summary>
        [DllImport("ole32.dll", PreserveSig = true)]
        public static extern void CoUninitialize();

        /// <summary>
        /// Creates a COM object instance.
        /// </summary>
        /// <param name="rclsid">Class identifier.</param>
        /// <param name="pUnkOuter">Outer unknown for aggregation.</param>
        /// <param name="dwClsContext">Class context.</param>
        /// <param name="riid">Interface identifier.</param>
        /// <param name="ppv">Receives the interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [DllImport("ole32.dll", PreserveSig = true)]
        public static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}