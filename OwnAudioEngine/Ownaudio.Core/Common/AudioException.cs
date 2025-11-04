using System;

namespace Ownaudio.Core.Common
{
    /// <summary>
    /// Categories of audio errors for structured error reporting.
    /// </summary>
    public enum AudioErrorCategory
    {
        /// <summary>Unknown or unspecified error.</summary>
        Unknown,

        /// <summary>Invalid or unsupported file format.</summary>
        FileFormat,

        /// <summary>I/O operation failed (read/write/seek).</summary>
        IO,

        /// <summary>Audio decoding failed.</summary>
        Decoding,

        /// <summary>Seek operation failed.</summary>
        Seeking,

        /// <summary>Platform-specific API call failed.</summary>
        PlatformAPI,

        /// <summary>Memory allocation or buffer management failed.</summary>
        OutOfMemory,

        /// <summary>Audio device operation failed.</summary>
        Device,

        /// <summary>Invalid configuration or parameters.</summary>
        Configuration
    }

    /// <summary>
    /// Exception thrown when audio operations fail.
    /// Provides structured error reporting with category, file path, and stream position.
    /// </summary>
    public class AudioException : Exception
    {
        /// <summary>
        /// Platform-specific error code.
        /// </summary>
        public int ErrorCode { get; }

        /// <summary>
        /// Category of the audio error.
        /// </summary>
        public AudioErrorCategory Category { get; }

        /// <summary>
        /// File path associated with the error (if applicable).
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Stream position where the error occurred (if applicable).
        /// </summary>
        public long? StreamPosition { get; set; }

        /// <summary>
        /// Creates a new AudioException with the specified message.
        /// </summary>
        public AudioException(string message) : base(message)
        {
            ErrorCode = -1;
            Category = AudioErrorCategory.Unknown;
        }

        /// <summary>
        /// Creates a new AudioException with the specified category and message.
        /// </summary>
        public AudioException(AudioErrorCategory category, string message) : base(message)
        {
            ErrorCode = -1;
            Category = category;
        }

        /// <summary>
        /// Creates a new AudioException with the specified message and error code.
        /// </summary>
        public AudioException(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
            Category = AudioErrorCategory.Unknown;
        }

        /// <summary>
        /// Creates a new AudioException with the specified category, message, and inner exception.
        /// </summary>
        public AudioException(AudioErrorCategory category, string message, Exception? innerException)
            : base(message, innerException)
        {
            ErrorCode = -1;
            Category = category;
        }

        /// <summary>
        /// Creates a new AudioException with the specified message and inner exception.
        /// </summary>
        public AudioException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = -1;
            Category = AudioErrorCategory.Unknown;
        }

        /// <summary>
        /// Creates a new AudioException with the specified message, error code, and inner exception.
        /// </summary>
        public AudioException(string message, int errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Category = AudioErrorCategory.Unknown;
        }

        /// <summary>
        /// Creates a new AudioException with complete error information.
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