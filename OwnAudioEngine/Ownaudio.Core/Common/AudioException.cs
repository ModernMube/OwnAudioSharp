using System;

namespace Ownaudio.Core.Common
{
    /// <summary>
    /// What kind of audio error we are dealing with.
    /// </summary>
    public enum AudioErrorCategory
    {
        /// <summary>
        /// We have no idea.
        /// </summary>
        Unknown,

        /// <summary>
        /// Bad or unsupported container/codec.
        /// </summary>
        FileFormat,

        /// <summary>
        /// Read, write or seek blew up.
        /// </summary>
        IO,

        /// <summary>
        /// Decoder failed on the stream.
        /// </summary>
        Decoding,

        /// <summary>
        /// Seek did not land where we wanted.
        /// </summary>
        Seeking,

        /// <summary>
        /// A native/platform call returned an error.
        /// </summary>
        PlatformAPI,

        /// <summary>
        /// Ran out of memory or the buffer handling gave up.
        /// </summary>
        OutOfMemory,

        /// <summary>
        /// Device open/start/stop trouble.
        /// </summary>
        Device,

        /// <summary>
        /// Config or parameters make no sense.
        /// </summary>
        Configuration
    }

    /// <summary>
    /// Audio failure with a bit of context attached — category, file, stream position.
    /// </summary>
    public class AudioException : Exception
    {
        /// <summary>
        /// Native error code, -1 when there is none.
        /// </summary>
        public int ErrorCode { get; }

        /// <summary>
        /// Rough bucket the failure falls into.
        /// </summary>
        public AudioErrorCategory Category { get; }

        /// <summary>
        /// File we choked on, if any.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Where we were in the stream when it happened.
        /// </summary>
        public long? StreamPosition { get; set; }

        /// <summary>
        /// Plain message, nothing else known.
        /// </summary>
        public AudioException(string message)
            : this(AudioErrorCategory.Unknown, message, -1) { }

        /// <summary></summary>
        public AudioException(AudioErrorCategory category, string message)
            : this(category, message, -1) { }

        /// <summary>
        /// Message plus a native code.
        /// </summary>
        public AudioException(string message, int errorCode)
            : this(AudioErrorCategory.Unknown, message, errorCode) { }

        /// <summary></summary>
        public AudioException(AudioErrorCategory category, string message, Exception? innerException)
            : this(category, message, -1, innerException) { }

        /// <summary>
        /// Wraps whatever threw underneath.
        /// </summary>
        public AudioException(string message, Exception innerException)
            : this(AudioErrorCategory.Unknown, message, -1, innerException) { }

        /// <summary></summary>
        public AudioException(string message, int errorCode, Exception innerException)
            : this(AudioErrorCategory.Unknown, message, errorCode, innerException) { }

        /// <summary>
        /// The one everything else funnels into.
        /// </summary>
        public AudioException(
            AudioErrorCategory category,
            string message,
            int errorCode,
            Exception? innerException = null,
            string? filePath = null,
            long? streamPosition = null)
            : base(message, innerException)
        {
            Category = category;
            ErrorCode = errorCode;
            FilePath = filePath;
            StreamPosition = streamPosition;
        }
    }
}
