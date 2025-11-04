using System;
using System.Runtime.InteropServices;

namespace Ownaudio.macOS.Interop
{
    /// <summary>
    /// P/Invoke declarations for macOS Core Audio framework using Audio Unit API.
    /// This provides low-level, zero-resampling audio I/O similar to WASAPI.
    /// </summary>
    internal static class CoreAudioInterop
    {
        private const string AudioToolboxFramework = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreAudioFramework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

        #region Audio Unit API

        /// <summary>
        /// Creates a new Audio Component Instance (Audio Unit).
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioComponentInstanceNew(
            IntPtr inComponent,
            out IntPtr outInstance);

        /// <summary>
        /// Disposes of an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioComponentInstanceDispose(IntPtr inInstance);

        /// <summary>
        /// Finds an Audio Component matching the description.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr AudioComponentFindNext(
            IntPtr inComponent,
            ref AudioComponentDescription inDesc);

        /// <summary>
        /// Initializes an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioUnitInitialize(IntPtr inUnit);

        /// <summary>
        /// Uninitializes an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioUnitUninitialize(IntPtr inUnit);

        /// <summary>
        /// Starts audio processing in an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioOutputUnitStart(IntPtr ci);

        /// <summary>
        /// Stops audio processing in an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioOutputUnitStop(IntPtr ci);

        /// <summary>
        /// Sets a property on an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioUnitSetProperty(
            IntPtr inUnit,
            uint inID,
            uint inScope,
            uint inElement,
            IntPtr inData,
            uint inDataSize);

        /// <summary>
        /// Gets a property from an Audio Unit.
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioUnitGetProperty(
            IntPtr inUnit,
            uint inID,
            uint inScope,
            uint inElement,
            IntPtr outData,
            ref uint ioDataSize);

        /// <summary>
        /// Renders audio from an Audio Unit (for input/capture).
        /// </summary>
        [DllImport(AudioToolboxFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioUnitRender(
            IntPtr inUnit,
            ref uint ioActionFlags,
            ref AudioTimeStamp inTimeStamp,
            uint inOutputBusNumber,
            uint inNumberFrames,
            IntPtr ioData);

        #endregion

        #region AudioObject API (for device enumeration)

        /// <summary>
        /// Gets the size of a property's data.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectGetPropertyDataSize(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize,
            IntPtr inQualifierData,
            out uint outDataSize);

        /// <summary>
        /// Gets property data from an audio object.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectGetPropertyData(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize,
            IntPtr inQualifierData,
            ref uint ioDataSize,
            IntPtr outData);

        /// <summary>
        /// Sets property data on an audio object.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectSetPropertyData(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize,
            IntPtr inQualifierData,
            uint inDataSize,
            IntPtr inData);

        /// <summary>
        /// Checks if an audio object has a property.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectHasProperty(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress);

        /// <summary>
        /// Adds a property listener to an audio object.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectAddPropertyListener(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress,
            AudioObjectPropertyListenerProc inListener,
            IntPtr inClientData);

        /// <summary>
        /// Removes a property listener from an audio object.
        /// </summary>
        [DllImport(CoreAudioFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectRemovePropertyListener(
            uint inObjectID,
            ref AudioObjectPropertyAddress inAddress,
            AudioObjectPropertyListenerProc inListener,
            IntPtr inClientData);

        #endregion

        #region CoreFoundation API

        /// <summary>
        /// Releases a CoreFoundation object.
        /// </summary>
        [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CFRelease(IntPtr cf);

        /// <summary>
        /// Gets the length of a CFString.
        /// </summary>
        [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CFStringGetLength(IntPtr theString);

        /// <summary>
        /// Gets characters from a CFString.
        /// </summary>
        [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool CFStringGetCString(
            IntPtr theString,
            IntPtr buffer,
            int bufferSize,
            uint encoding);

        #endregion

        #region Structs and Enums

        /// <summary>
        /// Describes the format of audio data.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioStreamBasicDescription
        {
            public double mSampleRate;
            public uint mFormatID;
            public uint mFormatFlags;
            public uint mBytesPerPacket;
            public uint mFramesPerPacket;
            public uint mBytesPerFrame;
            public uint mChannelsPerFrame;
            public uint mBitsPerChannel;
            public uint mReserved;

            /// <summary>
            /// Creates a Float32 PCM format description.
            /// </summary>
            public static AudioStreamBasicDescription CreateFloat32PCM(int sampleRate, int channels)
            {
                return new AudioStreamBasicDescription
                {
                    mSampleRate = sampleRate,
                    mFormatID = kAudioFormatLinearPCM,
                    mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                    mBytesPerPacket = 4,
                    mFramesPerPacket = 1,
                    mBytesPerFrame = 4,
                    mChannelsPerFrame = (uint)channels,
                    mBitsPerChannel = 32,
                    mReserved = 0
                };
            }

            /// <summary>
            /// Creates an interleaved Float32 PCM format description (for compatibility).
            /// </summary>
            public static AudioStreamBasicDescription CreateFloat32InterleavedPCM(int sampleRate, int channels)
            {
                return new AudioStreamBasicDescription
                {
                    mSampleRate = sampleRate,
                    mFormatID = kAudioFormatLinearPCM,
                    mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                    mBytesPerPacket = (uint)(4 * channels),
                    mFramesPerPacket = 1,
                    mBytesPerFrame = (uint)(4 * channels),
                    mChannelsPerFrame = (uint)channels,
                    mBitsPerChannel = 32,
                    mReserved = 0
                };
            }
        }

        /// <summary>
        /// Audio Component Description for finding Audio Units.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioComponentDescription
        {
            public uint componentType;
            public uint componentSubType;
            public uint componentManufacturer;
            public uint componentFlags;
            public uint componentFlagsMask;
        }

        /// <summary>
        /// Describes a property address for audio objects.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioObjectPropertyAddress
        {
            public uint mSelector;
            public uint mScope;
            public uint mElement;

            public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
            {
                mSelector = selector;
                mScope = scope;
                mElement = element;
            }
        }

        /// <summary>
        /// Audio buffer list for non-interleaved audio data.
        /// In C: struct AudioBufferList { UInt32 mNumberBuffers; AudioBuffer mBuffers[1]; }
        /// The first buffer is part of the struct, additional buffers follow in memory.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioBufferList
        {
            public uint mNumberBuffers;
            public CoreAudioBuffer mFirstBuffer; // First buffer is part of the struct!
            // Additional buffers (if mNumberBuffers > 1) follow immediately after this struct
        }

        /// <summary>
        /// Single audio buffer (Core Audio).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CoreAudioBuffer
        {
            public uint mNumberChannels;
            public uint mDataByteSize;
            public IntPtr mData;
        }

        /// <summary>
        /// Audio timestamp structure for synchronization.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioTimeStamp
        {
            public double mSampleTime;
            public ulong mHostTime;
            public double mRateScalar;
            public ulong mWordClockTime;
            public uint mSMPTETime_mSubframes;
            public uint mSMPTETime_mSubframeDivisor;
            public uint mSMPTETime_mCounter;
            public uint mSMPTETime_mType;
            public uint mSMPTETime_mFlags;
            public short mSMPTETime_mHours;
            public short mSMPTETime_mMinutes;
            public short mSMPTETime_mSeconds;
            public short mSMPTETime_mFrames;
            public uint mFlags;
            public uint mReserved;
        }

        #endregion

        #region Delegates

        /// <summary>
        /// Callback for Audio Unit output rendering.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int AURenderCallback(
            IntPtr inRefCon,
            ref uint ioActionFlags,
            ref AudioTimeStamp inTimeStamp,
            uint inBusNumber,
            uint inNumberFrames,
            IntPtr ioData);

        /// <summary>
        /// Callback for audio object property changes.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int AudioObjectPropertyListenerProc(
            uint inObjectID,
            uint inNumberAddresses,
            IntPtr inAddresses,
            IntPtr inClientData);

        #endregion

        #region Constants

        // Audio Format IDs
        internal const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'

        // Audio Format Flags
        internal const uint kAudioFormatFlagIsFloat = (1 << 0);
        internal const uint kAudioFormatFlagIsPacked = (1 << 3);
        internal const uint kAudioFormatFlagIsNonInterleaved = (1 << 5);

        // Audio Component Types
        internal const uint kAudioUnitType_Output = 0x61756F75; // 'auou'
        internal const uint kAudioUnitSubType_HALOutput = 0x6168616C; // 'ahal'
        internal const uint kAudioUnitManufacturer_Apple = 0x6170706C; // 'appl'

        // Audio Unit Properties
        internal const uint kAudioOutputUnitProperty_CurrentDevice = 2000;
        internal const uint kAudioUnitProperty_StreamFormat = 8;
        internal const uint kAudioOutputUnitProperty_EnableIO = 2003;
        internal const uint kAudioUnitProperty_SetRenderCallback = 23;
        internal const uint kAudioOutputUnitProperty_SetInputCallback = 2005;
        internal const uint kAudioUnitProperty_MaximumFramesPerSlice = 14;
        internal const uint kAudioDevicePropertyBufferFrameSize = 0x6673697A; // 'fsiz'

        // Audio Unit Scopes
        internal const uint kAudioUnitScope_Global = 0;
        internal const uint kAudioUnitScope_Input = 1;
        internal const uint kAudioUnitScope_Output = 2;

        // AudioObject System Object
        internal const uint kAudioObjectSystemObject = 1;

        // Property Selectors
        internal const uint kAudioHardwarePropertyDevices = 0x64657623; // 'dev#'
        internal const uint kAudioHardwarePropertyDefaultOutputDevice = 0x646F7574; // 'dOut'
        internal const uint kAudioHardwarePropertyDefaultInputDevice = 0x64496E20; // 'dIn '
        internal const uint kAudioDevicePropertyDeviceNameCFString = 0x6C6E616D; // 'lnam'
        internal const uint kAudioDevicePropertyStreams = 0x73746D23; // 'stm#'
        internal const uint kAudioDevicePropertyNominalSampleRate = 0x6E737274; // 'nsrt'
        internal const uint kAudioDevicePropertyPreferredChannelsForStereo = 0x64636832; // 'dch2'

        // Property Scopes
        internal const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62; // 'glob'
        internal const uint kAudioObjectPropertyScopeInput = 0x696E7074; // 'inpt'
        internal const uint kAudioObjectPropertyScopeOutput = 0x6F757470; // 'outp'

        // Property Elements
        internal const uint kAudioObjectPropertyElementMaster = 0;

        // Error Codes
        internal const int noErr = 0;
        internal const int kAudioUnitErr_InvalidProperty = -10879;
        internal const int kAudioUnitErr_InvalidParameter = -10878;
        internal const int kAudioUnitErr_InvalidElement = -10877;
        internal const int kAudioUnitErr_NoConnection = -10876;
        internal const int kAudioUnitErr_FailedInitialization = -10875;

        // CoreFoundation
        internal const uint kCFStringEncodingUTF8 = 0x08000100;

        #endregion

        #region Helper Methods

        /// <summary>
        /// Converts a CFString to a managed string.
        /// </summary>
        internal static string? CFStringToString(IntPtr cfString)
        {
            if (cfString == IntPtr.Zero)
                return null;

            int length = CFStringGetLength(cfString);
            if (length == 0)
                return string.Empty;

            int bufferSize = (length * 4) + 1; // UTF-8 worst case
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (CFStringGetCString(cfString, buffer, bufferSize, kCFStringEncodingUTF8))
                {
                    return Marshal.PtrToStringUTF8(buffer);
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Checks if an OSStatus (error code) indicates success.
        /// </summary>
        internal static bool IsSuccess(int status)
        {
            return status == noErr;
        }

        /// <summary>
        /// Gets a human-readable error message for an OSStatus code.
        /// </summary>
        internal static string GetErrorMessage(int status)
        {
            return status switch
            {
                noErr => "Success",
                kAudioUnitErr_InvalidProperty => "Invalid property",
                kAudioUnitErr_InvalidParameter => "Invalid parameter",
                kAudioUnitErr_InvalidElement => "Invalid element",
                kAudioUnitErr_NoConnection => "No connection",
                kAudioUnitErr_FailedInitialization => "Failed initialization",
                _ => $"Core Audio error: {status} (0x{status:X8})"
            };
        }

        /// <summary>
        /// Throws an exception if OSStatus indicates error.
        /// </summary>
        public static void ThrowIfError(int status, string operation)
        {
            if (status != noErr)
            {
                string errorMessage = GetErrorMessage(status);
                
                // Próbáljuk meg a FourCC kódot is kiolvasni, ha hibaként kódolható
                string fourCC = (status >= 0x20202020 && status <= 0x7A7A7A7A) 
                                ? new string(new[] { 
                                    (char)((status >> 24) & 0xFF), 
                                    (char)((status >> 16) & 0xFF), 
                                    (char)((status >> 8) & 0xFF), 
                                    (char)(status & 0xFF) 
                                }) 
                                : null;

                string codeInfo = string.IsNullOrEmpty(fourCC) 
                    ? $" ({status} / 0x{status:X8})"
                    : $" ({fourCC} / 0x{status:X8})";
                    
                throw new InvalidOperationException(
                    $"{operation} failed with OSStatus: {errorMessage}{codeInfo}");
            }
        }

        #endregion
    }
}
