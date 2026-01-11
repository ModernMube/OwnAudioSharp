using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop;

/// <summary>
/// P/Invoke declarations for Windows Media Foundation API.
/// Used for MP3 decoding with zero external dependencies.
/// </summary>
internal static class MediaFoundationInterop
{
    private const string MFPlatDll = "mfplat.dll";
    private const string MFReadWriteDll = "mfreadwrite.dll";

    // Media Foundation version constant
    public const uint MF_VERSION = 0x00020070; // MF_SDK_VERSION for Windows 10
    public const uint MF_STARTUP_LITE = 0x00000001;
    public const uint MFSTARTUP_NOSOCKET = 0x00000001;
    public const uint MFSTARTUP_FULL = 0x00000000;

    #region Media Foundation Startup/Shutdown

    /// <summary>
    /// Initializes Microsoft Media Foundation.
    /// </summary>
    [DllImport(MFPlatDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFStartup(uint version, uint dwFlags = MFSTARTUP_NOSOCKET);

    /// <summary>
    /// Shuts down the Media Foundation platform.
    /// </summary>
    [DllImport(MFPlatDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFShutdown();

    #endregion

    #region Source Reader Creation

    /// <summary>
    /// Creates the source reader from a URL.
    /// </summary>
    [DllImport(MFReadWriteDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IntPtr ppSourceReader);

    /// <summary>
    /// Creates the source reader from a byte stream.
    /// </summary>
    [DllImport(MFReadWriteDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateSourceReaderFromByteStream(
        IntPtr pByteStream,
        IntPtr pAttributes,
        out IntPtr ppSourceReader);

    #endregion

    #region Media Type Creation

    /// <summary>
    /// Creates an empty media type.
    /// </summary>
    [DllImport(MFPlatDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMediaType(out IntPtr ppMFType);

    #endregion

    #region Stream Indices

    public const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);
    public const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);
    public const int MF_SOURCE_READER_ANY_STREAM = unchecked((int)0xFFFFFFFE);
    public const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xFFFFFFFF);

    #endregion

    #region Source Reader Flags

    [Flags]
    public enum MF_SOURCE_READER_FLAG : uint
    {
        None = 0,
        Error = 0x00000001,
        EndOfStream = 0x00000002,
        NewStream = 0x00000004,
        NativeMediaTypeChanged = 0x00000010,
        CurrentMediaTypeChanged = 0x00000020,
        StreamTick = 0x00000100,
        AllEffectsRemoved = 0x00000200
    }

    #endregion

    #region Media Type GUIDs

    public static class MFMediaType
    {
        public static readonly Guid Audio = new Guid(0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        public static readonly Guid Video = new Guid(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }

    public static class MFAudioFormat
    {
        public static readonly Guid PCM = new Guid(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        public static readonly Guid Float = new Guid(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        public static readonly Guid MP3 = new Guid(0x00000055, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }

    #endregion

    #region Attribute GUIDs

    public static class MFAttributeKeys
    {
        // Major type
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);

        // Subtype (audio format)
        public static readonly Guid MF_MT_SUBTYPE = new Guid(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);

        // Audio samples per second
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid(0x5faeeae7, 0x0290, 0x4c31, 0x9e, 0x8a, 0xc5, 0x34, 0xf6, 0x8d, 0x9d, 0xba);

        // Audio number of channels
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid(0x37e48bf5, 0x645e, 0x4c5b, 0x89, 0xde, 0xad, 0xa9, 0xe2, 0x9b, 0x69, 0x6a);

        // Audio bits per sample
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid(0xf2deb57f, 0x40fa, 0x4764, 0xaa, 0x33, 0xed, 0x4f, 0x2d, 0x1f, 0xf6, 0x69);

        // Audio block alignment
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid(0x322de230, 0x9eeb, 0x43bd, 0xab, 0x7a, 0xff, 0x41, 0x22, 0x51, 0x54, 0x1d);

        // Audio average bytes per second
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid(0x1aab75c8, 0xcfef, 0x451c, 0xab, 0x95, 0xac, 0x03, 0x4b, 0x8e, 0x17, 0x31);

        // Duration (in 100-nanosecond units)
        public static readonly Guid MF_PD_DURATION = new Guid(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);
    }

    #endregion

    #region IMFAttributes Interface

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        void GetItem(ref Guid guidKey, IntPtr pValue);
        void GetItemType(ref Guid guidKey, out uint pType);
        void CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        void Compare(IMFAttributes pTheirs, uint matchType, out bool pbResult);
        void GetUINT32(ref Guid guidKey, out uint punValue);
        void GetUINT64(ref Guid guidKey, out ulong punValue);
        void GetDouble(ref Guid guidKey, out double pfValue);
        void GetGUID(ref Guid guidKey, out Guid pguidValue);
        void GetStringLength(ref Guid guidKey, out uint pcchLength);
        void GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
        void GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        void GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        void GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, IntPtr pcbBlobSize);
        void GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        void GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);

        void SetItem(ref Guid guidKey, IntPtr value);
        void DeleteItem(ref Guid guidKey);
        void DeleteAllItems();
        void SetUINT32(ref Guid guidKey, uint unValue);
        void SetUINT64(ref Guid guidKey, ulong unValue);
        void SetDouble(ref Guid guidKey, double fValue);
        void SetGUID(ref Guid guidKey, ref Guid guidValue);
        void SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        void SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        void SetUnknown(ref Guid guidKey, IntPtr pUnknown);

        void LockStore();
        void UnlockStore();
        void GetCount(out uint pcItems);
        void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        void CopyAllItems(IMFAttributes pDest);
    }

    #endregion

    #region IMFMediaType Interface

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType : IMFAttributes
    {
        #region IMFAttributes methods (inherited)
        new void GetItem(ref Guid guidKey, IntPtr pValue);
        new void GetItemType(ref Guid guidKey, out uint pType);
        new void CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        new void Compare(IMFAttributes pTheirs, uint matchType, out bool pbResult);
        new void GetUINT32(ref Guid guidKey, out uint punValue);
        new void GetUINT64(ref Guid guidKey, out ulong punValue);
        new void GetDouble(ref Guid guidKey, out double pfValue);
        new void GetGUID(ref Guid guidKey, out Guid pguidValue);
        new void GetStringLength(ref Guid guidKey, out uint pcchLength);
        new void GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
        new void GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        new void GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new void GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, IntPtr pcbBlobSize);
        new void GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        new void GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        new void SetItem(ref Guid guidKey, IntPtr value);
        new void DeleteItem(ref Guid guidKey);
        new void DeleteAllItems();
        new void SetUINT32(ref Guid guidKey, uint unValue);
        new void SetUINT64(ref Guid guidKey, ulong unValue);
        new void SetDouble(ref Guid guidKey, double fValue);
        new void SetGUID(ref Guid guidKey, ref Guid guidValue);
        new void SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new void SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        new void SetUnknown(ref Guid guidKey, IntPtr pUnknown);
        new void LockStore();
        new void UnlockStore();
        new void GetCount(out uint pcItems);
        new void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new void CopyAllItems(IMFAttributes pDest);
        #endregion

        void GetMajorType(out Guid pguidMajorType);
        void IsCompressedFormat(out bool pfCompressed);
        void IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        void GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        void FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    #endregion

    #region IMFMediaBuffer Interface

    [ComImport]
    [Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        void Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        void Unlock();
        void GetCurrentLength(out int pcbCurrentLength);
        void SetCurrentLength(int cbCurrentLength);
        void GetMaxLength(out int pcbMaxLength);
    }

    #endregion

    #region IMFSample Interface

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample : IMFAttributes
    {
        #region IMFAttributes methods (inherited)
        new void GetItem(ref Guid guidKey, IntPtr pValue);
        new void GetItemType(ref Guid guidKey, out uint pType);
        new void CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        new void Compare(IMFAttributes pTheirs, uint matchType, out bool pbResult);
        new void GetUINT32(ref Guid guidKey, out uint punValue);
        new void GetUINT64(ref Guid guidKey, out ulong punValue);
        new void GetDouble(ref Guid guidKey, out double pfValue);
        new void GetGUID(ref Guid guidKey, out Guid pguidValue);
        new void GetStringLength(ref Guid guidKey, out uint pcchLength);
        new void GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, IntPtr pcchLength);
        new void GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        new void GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new void GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, IntPtr pcbBlobSize);
        new void GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        new void GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        new void SetItem(ref Guid guidKey, IntPtr value);
        new void DeleteItem(ref Guid guidKey);
        new void DeleteAllItems();
        new void SetUINT32(ref Guid guidKey, uint unValue);
        new void SetUINT64(ref Guid guidKey, ulong unValue);
        new void SetDouble(ref Guid guidKey, double fValue);
        new void SetGUID(ref Guid guidKey, ref Guid guidValue);
        new void SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new void SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
        new void SetUnknown(ref Guid guidKey, IntPtr pUnknown);
        new void LockStore();
        new void UnlockStore();
        new void GetCount(out uint pcItems);
        new void GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new void CopyAllItems(IMFAttributes pDest);
        #endregion

        void GetSampleFlags(out uint pdwSampleFlags);
        void SetSampleFlags(uint dwSampleFlags);
        void GetSampleTime(out long phnsSampleTime);
        void SetSampleTime(long hnsSampleTime);
        void GetSampleDuration(out long phnsSampleDuration);
        void SetSampleDuration(long hnsSampleDuration);
        void GetBufferCount(out int pdwBufferCount);
        void GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        void AddBuffer(IMFMediaBuffer pBuffer);
        void RemoveBufferByIndex(int dwIndex);
        void RemoveAllBuffers();
        void GetTotalLength(out int pcbTotalLength);
        void CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    #endregion

    #region IMFSourceReader Interface

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReader
    {
        void GetStreamSelection(int dwStreamIndex, out bool pfSelected);
        void SetStreamSelection(int dwStreamIndex, bool fSelected);
        void GetNativeMediaType(int dwStreamIndex, int dwMediaTypeIndex, out IMFMediaType ppMediaType);
        void GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
        void SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
        void SetCurrentPosition(ref Guid guidTimeFormat, IntPtr varPosition);
        [PreserveSig]
        int ReadSample(
            int dwStreamIndex,
            int dwControlFlags,
            out int pdwActualStreamIndex,
            out MF_SOURCE_READER_FLAG pdwStreamFlags,
            out long pllTimestamp,
            out IMFSample ppSample);
        void Flush(int dwStreamIndex);
        void GetServiceForStream(int dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        void GetPresentationAttribute(int dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
    }

    #endregion

    #region PROPVARIANT for Seeking

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public long hVal; // VT_I8
        [FieldOffset(8)] public ulong uhVal; // VT_UI8
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(16)] public IntPtr ptr2; // For additional data

        public const ushort VT_I8 = 20;
        public const ushort VT_UI8 = 21;
        public const ushort VT_EMPTY = 0;

        public static PROPVARIANT FromInt64(long value)
        {
            return new PROPVARIANT
            {
                vt = VT_I8,
                hVal = value
            };
        }

        public static PROPVARIANT Empty()
        {
            return new PROPVARIANT
            {
                vt = VT_EMPTY
            };
        }
    }

    #endregion

    #region HRESULT Constants

    public static class HRESULT
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int MF_E_END_OF_STREAM = unchecked((int)0xC00D3E84);
        public const int MF_E_INVALIDREQUEST = unchecked((int)0xC00D36B2);
    }

    #endregion

    #region IMFSourceReaderCallback Interface

    /// <summary>
    /// Callback interface for asynchronous operations on the Source Reader.
    /// Enables event-driven, non-blocking audio decoding to eliminate busy-waiting CPU usage.
    /// </summary>
    [ComImport]
    [Guid("deec8d99-fa1d-4d82-84c4-2b7c95e1d50e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReaderCallback
    {
        /// <summary>
        /// Called when the IMFSourceReader.ReadSample method completes.
        /// </summary>
        /// <param name="hrStatus">Status code. If an error occurred, this is a failure code.</param>
        /// <param name="dwStreamIndex">The stream index.</param>
        /// <param name="dwStreamFlags">Bitwise OR of zero or more flags from MF_SOURCE_READER_FLAG.</param>
        /// <param name="llTimestamp">The time stamp of the sample, in 100-nanosecond units.</param>
        /// <param name="pSample">Pointer to IMFSample interface (can be null).</param>
        [PreserveSig]
        int OnReadSample(
            int hrStatus,
            uint dwStreamIndex,
            uint dwStreamFlags,
            long llTimestamp,
            IntPtr pSample);

        /// <summary>
        /// Called when the IMFSourceReader.Flush method completes.
        /// </summary>
        /// <param name="dwStreamIndex">The stream index.</param>
        [PreserveSig]
        int OnFlush(uint dwStreamIndex);

        /// <summary>
        /// Called when the source reader receives certain events from the media source.
        /// </summary>
        /// <param name="dwStreamIndex">The stream index.</param>
        /// <param name="pEvent">Pointer to IMFMediaEvent interface.</param>
        [PreserveSig]
        int OnEvent(uint dwStreamIndex, IntPtr pEvent);
    }

    #endregion

    #region Async Source Reader Attribute Keys

    public static class MFAsyncAttributeKeys
    {
        /// <summary>
        /// Sets the callback pointer for asynchronous operations.
        /// GUID: {1e3dbeac-bb43-4c35-b507-b582469461cd}
        /// </summary>
        public static readonly Guid MF_SOURCE_READER_ASYNC_CALLBACK =
            new Guid(0x1e3dbeac, 0xbb43, 0x4c35, 0xb5, 0x07, 0xb5, 0x82, 0x46, 0x94, 0x61, 0xcd);
    }

    #endregion

    #region Attributes Creation

    /// <summary>
    /// Creates an empty attribute store.
    /// </summary>
    [DllImport(MFPlatDll, ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if HRESULT indicates success.
    /// </summary>
    public static bool SUCCEEDED(int hr) => hr >= 0;

    /// <summary>
    /// Checks if HRESULT indicates failure.
    /// </summary>
    public static bool FAILED(int hr) => hr < 0;

    /// <summary>
    /// Throws exception if HRESULT indicates failure.
    /// </summary>
    public static void ThrowIfFailed(int hr, string? message = null)
    {
        if (FAILED(hr))
        {
            string errorMsg = message ?? $"Media Foundation operation failed with HRESULT 0x{hr:X8}";
            throw new COMException(errorMsg, hr);
        }
    }

    #endregion
}
