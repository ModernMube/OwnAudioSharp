using System;
using System.Runtime.InteropServices;

namespace Ownaudio.macOS.Interop;

/// <summary>
/// P/Invoke declarations for macOS AudioToolbox framework (ExtAudioFile API).
/// Used for audio file decoding (MP3, AAC, ALAC, etc.) with zero external dependencies.
/// </summary>
/// <remarks>
/// <para><b>Framework:</b> AudioToolbox.framework (part of Core Audio)</para>
/// <para><b>API Documentation:</b> https://developer.apple.com/documentation/audiotoolbox</para>
/// <para><b>Supported Formats:</b> MP3, AAC, ALAC, WAV, AIFF, CAF, and more</para>
/// <para><b>Thread Safety:</b> ExtAudioFile instances are NOT thread-safe</para>
/// </remarks>
internal static class AudioToolboxInterop
{
    private const string AudioToolboxFramework = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    #region ExtAudioFile Types

    /// <summary>
    /// ExtAudioFile opaque reference type.
    /// </summary>
    public struct ExtAudioFileRef
    {
        public IntPtr Handle;

        public bool IsValid => Handle != IntPtr.Zero;

        public static ExtAudioFileRef Invalid => new ExtAudioFileRef { Handle = IntPtr.Zero };
    }

    #endregion

    #region ExtAudioFile Property IDs

    /// <summary>
    /// ExtAudioFile property IDs.
    /// </summary>
    public static class ExtAudioFilePropertyID
    {
        /// <summary>
        /// kExtAudioFileProperty_FileDataFormat
        /// Type: AudioStreamBasicDescription
        /// The format of the audio data in the file (read-only).
        /// </summary>
        public const uint FileDataFormat = 0x66666d74; // 'ffmt'

        /// <summary>
        /// kExtAudioFileProperty_ClientDataFormat
        /// Type: AudioStreamBasicDescription
        /// The format of the audio data you want to read/write (read-write).
        /// ExtAudioFile will perform conversion if different from file format.
        /// </summary>
        public const uint ClientDataFormat = 0x63666d74; // 'cfmt'

        /// <summary>
        /// kExtAudioFileProperty_FileLengthFrames
        /// Type: SInt64
        /// The total number of sample frames in the file (read-only).
        /// </summary>
        public const uint FileLengthFrames = 0x2366726d; // '#frm'

        /// <summary>
        /// kExtAudioFileProperty_FileChannelLayout
        /// Type: AudioChannelLayout
        /// The channel layout of the file (optional).
        /// </summary>
        public const uint FileChannelLayout = 0x66636c6f; // 'fclo'

        /// <summary>
        /// kExtAudioFileProperty_ClientChannelLayout
        /// Type: AudioChannelLayout
        /// The channel layout for client data (optional).
        /// </summary>
        public const uint ClientChannelLayout = 0x63636c6f; // 'cclo'
    }

    #endregion

    #region AudioStreamBasicDescription

    /// <summary>
    /// AudioStreamBasicDescription (ASBD) structure.
    /// Describes the format of audio data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        /// <summary>
        /// Sample rate (Hz)
        /// </summary>
        public double SampleRate;

        /// <summary>
        /// Format ID (kAudioFormatLinearPCM, kAudioFormatMPEGLayer3, etc.)
        /// </summary>
        public uint FormatID;

        /// <summary>
        /// Format flags (kAudioFormatFlagIsFloat, kAudioFormatFlagIsBigEndian, etc.)
        /// </summary>
        public uint FormatFlags;

        /// <summary>
        /// Bytes per packet (for PCM = bytes per frame)
        /// </summary>
        public uint BytesPerPacket;

        /// <summary>
        /// Frames per packet (for PCM = 1)
        /// </summary>
        public uint FramesPerPacket;

        /// <summary>
        /// Bytes per frame (channels * bytes per sample)
        /// </summary>
        public uint BytesPerFrame;

        /// <summary>
        /// Number of channels
        /// </summary>
        public uint ChannelsPerFrame;

        /// <summary>
        /// Bits per channel
        /// </summary>
        public uint BitsPerChannel;

        /// <summary>
        /// Reserved (must be 0)
        /// </summary>
        public uint Reserved;

        /// <summary>
        /// Creates ASBD for Float32 PCM format (most common for processing).
        /// </summary>
        public static AudioStreamBasicDescription CreateFloat32(int sampleRate, int channels)
        {
            return new AudioStreamBasicDescription
            {
                SampleRate = sampleRate,
                FormatID = AudioFormatID.LinearPCM,
                FormatFlags = (uint)(AudioFormatFlags.Float | AudioFormatFlags.Packed | AudioFormatFlags.NativeEndian),
                BytesPerPacket = (uint)(channels * sizeof(float)),
                FramesPerPacket = 1,
                BytesPerFrame = (uint)(channels * sizeof(float)),
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 32,
                Reserved = 0
            };
        }

        /// <summary>
        /// Creates ASBD for Int16 PCM format.
        /// </summary>
        public static AudioStreamBasicDescription CreateInt16(int sampleRate, int channels)
        {
            return new AudioStreamBasicDescription
            {
                SampleRate = sampleRate,
                FormatID = AudioFormatID.LinearPCM,
                FormatFlags = (uint)(AudioFormatFlags.SignedInteger | AudioFormatFlags.Packed | AudioFormatFlags.NativeEndian),
                BytesPerPacket = (uint)(channels * sizeof(short)),
                FramesPerPacket = 1,
                BytesPerFrame = (uint)(channels * sizeof(short)),
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 16,
                Reserved = 0
            };
        }
    }

    #endregion

    #region AudioFormatID Constants

    /// <summary>
    /// Audio format IDs (FourCC codes).
    /// </summary>
    public static class AudioFormatID
    {
        /// <summary>
        /// kAudioFormatLinearPCM - Linear PCM (uncompressed)
        /// </summary>
        public const uint LinearPCM = 0x6C70636D; // 'lpcm'

        /// <summary>
        /// kAudioFormatMPEGLayer3 - MP3
        /// </summary>
        public const uint MPEGLayer3 = 0x2E6D7033; // '.mp3'

        /// <summary>
        /// kAudioFormatMPEG4AAC - AAC
        /// </summary>
        public const uint MPEG4AAC = 0x61616320; // 'aac '

        /// <summary>
        /// kAudioFormatAppleLossless - ALAC
        /// </summary>
        public const uint AppleLossless = 0x616C6163; // 'alac'

        /// <summary>
        /// kAudioFormatFLAC - FLAC
        /// </summary>
        public const uint FLAC = 0x666C6163; // 'flac'
    }

    #endregion

    #region AudioFormatFlags Constants

    /// <summary>
    /// Audio format flags for LinearPCM.
    /// </summary>
    [Flags]
    public enum AudioFormatFlags : uint
    {
        /// <summary>
        /// kAudioFormatFlagIsFloat - Samples are floating point values
        /// </summary>
        Float = (1u << 0),

        /// <summary>
        /// kAudioFormatFlagIsBigEndian - Samples are big endian
        /// </summary>
        BigEndian = (1u << 1),

        /// <summary>
        /// kAudioFormatFlagIsSignedInteger - Samples are signed integers
        /// </summary>
        SignedInteger = (1u << 2),

        /// <summary>
        /// kAudioFormatFlagIsPacked - Samples are packed with no gaps
        /// </summary>
        Packed = (1u << 3),

        /// <summary>
        /// kAudioFormatFlagIsAlignedHigh - Samples are high-aligned in container
        /// </summary>
        AlignedHigh = (1u << 4),

        /// <summary>
        /// kAudioFormatFlagIsNonInterleaved - Samples are non-interleaved (planar)
        /// </summary>
        NonInterleaved = (1u << 5),

        /// <summary>
        /// kAudioFormatFlagIsNonMixable - Format is non-mixable
        /// </summary>
        NonMixable = (1u << 6),

        /// <summary>
        /// Native endian (little-endian on Intel Macs, big-endian on PowerPC)
        /// </summary>
        NativeEndian = 0, // Little-endian on all current Macs

        /// <summary>
        /// kAudioFormatFlagsNativeFloatPacked - Native float packed
        /// Common format: Float32, native endian, packed
        /// </summary>
        NativeFloatPacked = Float | Packed | NativeEndian
    }

    #endregion

    #region AudioBufferList

    /// <summary>
    /// AudioBuffer - single audio buffer in a buffer list.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        /// <summary>
        /// Number of interleaved channels in the buffer
        /// </summary>
        public uint NumberChannels;

        /// <summary>
        /// Size of data in bytes
        /// </summary>
        public uint DataByteSize;

        /// <summary>
        /// Pointer to audio data
        /// </summary>
        public IntPtr Data;
    }

    /// <summary>
    /// AudioBufferList - variable-length structure containing one or more AudioBuffer.
    /// For interleaved audio, typically contains 1 buffer.
    /// For non-interleaved (planar) audio, contains 1 buffer per channel.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        /// <summary>
        /// Number of buffers in the list
        /// </summary>
        public uint NumberBuffers;

        /// <summary>
        /// First buffer (use pointer arithmetic for additional buffers)
        /// </summary>
        public AudioBuffer Buffer0;

        /// <summary>
        /// Creates an AudioBufferList for interleaved audio (single buffer).
        /// </summary>
        /// <param name="channels">Number of channels</param>
        /// <param name="dataPtr">Pointer to audio data buffer</param>
        /// <param name="dataByteSize">Size of data buffer in bytes</param>
        public static AudioBufferList CreateInterleaved(uint channels, IntPtr dataPtr, uint dataByteSize)
        {
            return new AudioBufferList
            {
                NumberBuffers = 1,
                Buffer0 = new AudioBuffer
                {
                    NumberChannels = channels,
                    DataByteSize = dataByteSize,
                    Data = dataPtr
                }
            };
        }
    }

    #endregion

    #region OSStatus Error Codes

    /// <summary>
    /// Common OSStatus error codes.
    /// </summary>
    public static class OSStatus
    {
        /// <summary>
        /// noErr - Success
        /// </summary>
        public const int NoError = 0;

        /// <summary>
        /// kAudioFileUnsupportedFileTypeError
        /// </summary>
        public const int UnsupportedFileType = 0x7479703F; // 'typ?'

        /// <summary>
        /// kAudioFileUnsupportedDataFormatError
        /// </summary>
        public const int UnsupportedDataFormat = 0x666D743F; // 'fmt?'

        /// <summary>
        /// kAudioFileInvalidFileError
        /// </summary>
        public const int InvalidFile = 0x6474613F; // 'dta?'

        /// <summary>
        /// kAudioFileEndOfFileError
        /// </summary>
        public const int EndOfFile = -39; // eofErr

        /// <summary>
        /// kAudioFilePermissionsError
        /// </summary>
        public const int Permissions = -54; // permErr

        /// <summary>
        /// kAudioFileNotOpenError
        /// </summary>
        public const int NotOpen = -38;

        /// <summary>
        /// paramErr - Invalid parameter
        /// </summary>
        public const int ParamError = -50;

        /// <summary>
        /// memFullErr - Out of memory
        /// </summary>
        public const int MemoryFull = -108;

        /// <summary>
        /// Checks if OSStatus indicates success.
        /// </summary>
        public static bool IsSuccess(int status) => status == NoError;

        /// <summary>
        /// Checks if OSStatus indicates error.
        /// </summary>
        public static bool IsError(int status) => status != NoError;

        /// <summary>
        /// Converts OSStatus to FourCC string representation (for debugging).
        /// </summary>
        public static string ToFourCC(int status)
        {
            if (status == NoError)
                return "noErr";

            // Try to interpret as FourCC
            uint uStatus = unchecked((uint)status);
            char c1 = (char)((uStatus >> 24) & 0xFF);
            char c2 = (char)((uStatus >> 16) & 0xFF);
            char c3 = (char)((uStatus >> 8) & 0xFF);
            char c4 = (char)(uStatus & 0xFF);

            if (char.IsLetterOrDigit(c1) && char.IsLetterOrDigit(c2) &&
                char.IsLetterOrDigit(c3) && char.IsLetterOrDigit(c4))
            {
                return $"'{c1}{c2}{c3}{c4}'";
            }

            return status.ToString();
        }
    }

    #endregion

    #region ExtAudioFile API Functions

    /// <summary>
    /// Opens an audio file from a URL for reading.
    /// </summary>
    /// <param name="inURL">CFURLRef to the file to open</param>
    /// <param name="outExtAudioFile">On output, the ExtAudioFileRef</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileOpenURL")]
    public static extern int ExtAudioFileOpenURL(
        IntPtr inURL,
        out ExtAudioFileRef outExtAudioFile);

    /// <summary>
    /// Disposes of an ExtAudioFile object.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile to dispose</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileDispose")]
    public static extern int ExtAudioFileDispose(
        ExtAudioFileRef inExtAudioFile);

    /// <summary>
    /// Reads audio data from an ExtAudioFile.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile to read from</param>
    /// <param name="ioNumberFrames">On input, number of frames to read. On output, number actually read.</param>
    /// <param name="ioData">Buffer to receive audio data</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileRead")]
    public static extern int ExtAudioFileRead(
        ExtAudioFileRef inExtAudioFile,
        ref uint ioNumberFrames,
        ref AudioBufferList ioData);

    /// <summary>
    /// Seeks to a specific frame position in the file.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile</param>
    /// <param name="inFrameOffset">Frame position to seek to</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileSeek")]
    public static extern int ExtAudioFileSeek(
        ExtAudioFileRef inExtAudioFile,
        long inFrameOffset);

    /// <summary>
    /// Gets the current read/write position.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile</param>
    /// <param name="outFrameOffset">On output, the current frame position</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileTell")]
    public static extern int ExtAudioFileTell(
        ExtAudioFileRef inExtAudioFile,
        out long outFrameOffset);

    /// <summary>
    /// Gets the value of a property.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile</param>
    /// <param name="inPropertyID">Property ID to get</param>
    /// <param name="ioPropertyDataSize">On input, size of buffer. On output, actual size.</param>
    /// <param name="outPropertyData">Buffer to receive property value</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileGetProperty")]
    public static extern int ExtAudioFileGetProperty(
        ExtAudioFileRef inExtAudioFile,
        uint inPropertyID,
        ref uint ioPropertyDataSize,
        IntPtr outPropertyData);

    /// <summary>
    /// Sets the value of a property.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile</param>
    /// <param name="inPropertyID">Property ID to set</param>
    /// <param name="inPropertyDataSize">Size of property data</param>
    /// <param name="inPropertyData">Property data to set</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileSetProperty")]
    public static extern int ExtAudioFileSetProperty(
        ExtAudioFileRef inExtAudioFile,
        uint inPropertyID,
        uint inPropertyDataSize,
        IntPtr inPropertyData);

    /// <summary>
    /// Gets the size of a property value.
    /// </summary>
    /// <param name="inExtAudioFile">The ExtAudioFile</param>
    /// <param name="inPropertyID">Property ID</param>
    /// <param name="outSize">On output, the size of the property value</param>
    /// <param name="outWritable">On output, indicates if the property is writable</param>
    /// <returns>OSStatus - noErr (0) on success</returns>
    [DllImport(AudioToolboxFramework, EntryPoint = "ExtAudioFileGetPropertyInfo")]
    public static extern int ExtAudioFileGetPropertyInfo(
        ExtAudioFileRef inExtAudioFile,
        uint inPropertyID,
        out uint outSize,
        IntPtr outWritable);

    #endregion

    #region CoreFoundation Types and Functions (for CFURLRef)

    /// <summary>
    /// Creates a CFURL from a file system path.
    /// </summary>
    /// <param name="allocator">CFAllocator (pass IntPtr.Zero for default)</param>
    /// <param name="buffer">Byte representation of file system path</param>
    /// <param name="bufLen">Length of buffer</param>
    /// <param name="isDirectory">True if the path represents a directory</param>
    /// <returns>CFURLRef (must be released with CFRelease)</returns>
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        EntryPoint = "CFURLCreateFromFileSystemRepresentation")]
    public static extern IntPtr CFURLCreateFromFileSystemRepresentation(
        IntPtr allocator,
        IntPtr buffer,
        long bufLen,
        bool isDirectory);

    /// <summary>
    /// Creates a CFString from a C# string.
    /// </summary>
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        EntryPoint = "CFStringCreateWithCString")]
    public static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        string cStr,
        uint encoding); // kCFStringEncodingUTF8 = 0x08000100

    /// <summary>
    /// Releases a CoreFoundation object.
    /// </summary>
    /// <param name="cf">CFTypeRef to release</param>
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        EntryPoint = "CFRelease")]
    public static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// kCFStringEncodingUTF8
    /// </summary>
    public const uint kCFStringEncodingUTF8 = 0x08000100;

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a CFURLRef from a file path string.
    /// </summary>
    /// <param name="filePath">File path (must be absolute)</param>
    /// <returns>CFURLRef (must be released with CFRelease)</returns>
    public static IntPtr CreateCFURLFromPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        // Convert string to UTF-8 bytes
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(filePath);

        // Allocate unmanaged memory for path
        IntPtr pathPtr = Marshal.AllocHGlobal(pathBytes.Length);
        try
        {
            Marshal.Copy(pathBytes, 0, pathPtr, pathBytes.Length);

            // Create CFURL
            IntPtr urlRef = CFURLCreateFromFileSystemRepresentation(
                IntPtr.Zero,
                pathPtr,
                pathBytes.Length,
                false); // Not a directory

            return urlRef;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    // /// <summary>
    // /// Throws an exception if OSStatus indicates error.
    // /// </summary>
    // public static void ThrowIfError(int status, string operation)
    // {
    //     if (OSStatus.IsError(status))
    //     {
    //         string fourCC = OSStatus.ToFourCC(status);
    //         throw new InvalidOperationException(
    //             $"{operation} failed with OSStatus: {status} ({fourCC})");
    //     }
    // }

    #endregion
}
