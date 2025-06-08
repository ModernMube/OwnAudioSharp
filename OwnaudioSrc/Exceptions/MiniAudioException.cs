using System;
using System.Runtime.InteropServices;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.Exceptions;

/// <summary>
/// Represents errors that occur during interaction with the miniaudio library.
/// </summary>
public class MiniaudioException : Exception
{
    /// <summary>
    /// Gets the miniaudio result code associated with this exception.
    /// </summary>
    internal MaResult MaResultCode { get; }

    /// <summary>
    /// Gets the integer value of the miniaudio result code.
    /// </summary>
    public int ResultCode => (int)MaResultCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniaudioException"/> class.
    /// </summary>
    public MiniaudioException() : base("An error occurred during miniaudio operation.")
    {
        MaResultCode = MaResult.Error;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniaudioException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public MiniaudioException(string message) : base(message)
    {
        MaResultCode = MaResult.Error;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniaudioException"/> class with a specified error
    /// message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public MiniaudioException(string message, Exception innerException) : base(message, innerException)
    {
        MaResultCode = MaResult.Error;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniaudioException"/> class with a specified
    /// error message and miniaudio result code.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="resultCode">The miniaudio result code associated with this exception.</param>
    internal MiniaudioException(string message, MaResult resultCode) : base(FormatMessage(message, resultCode))
    {
        MaResultCode = resultCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MiniaudioException"/> class with a specified
    /// error message and integer result code.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="resultCode">The integer result code associated with this exception.</param>
    public MiniaudioException(string message, int resultCode) : base(FormatMessage(message, (MaResult)resultCode))
    {
        MaResultCode = (MaResult)resultCode;
    }

    /// <summary>
    /// Formats an error message to include the miniaudio error description.
    /// </summary>
    /// <param name="message">The base error message.</param>
    /// <param name="resultCode">The miniaudio result code.</param>
    /// <returns>A formatted error message that includes the miniaudio error description.</returns>
    private static string FormatMessage(string message, MaResult resultCode)
    {
        string errorDescription = GetErrorDescription(resultCode);
        return $"{message} (Miniaudio error: {(int)resultCode} - {errorDescription})";
    }

    /// <summary>
    /// Gets the error description for a miniaudio result code.
    /// </summary>
    /// <param name="resultCode">The miniaudio result code.</param>
    /// <returns>A string describing the error.</returns>
    private static string GetErrorDescription(MaResult resultCode)
    {
        IntPtr errorStringPtr = ma_result_description(resultCode);

        if (errorStringPtr == IntPtr.Zero)
            return "Unknown error";

        return Marshal.PtrToStringAnsi(errorStringPtr) ?? "Unknown error";
    }

    /// <summary>
    /// Throws a MiniaudioException if the specified result code indicates an error.
    /// </summary>
    /// <param name="result">The integer result code to check.</param>
    /// <param name="message">The error message to use if an exception is thrown.</param>
    /// <exception cref="MiniaudioException">Thrown when the result code is not 0 (MA_SUCCESS).</exception>
    public static void ThrowIfError(int result, string message)
    {
        if (result != 0) // MA_SUCCESS = 0
        {
            throw new MiniaudioException(message, result);
        }
    }

    /// <summary>
    /// Throws a MiniaudioException if the specified result code indicates an error.
    /// </summary>
    /// <param name="result">The miniaudio result code to check.</param>
    /// <param name="message">The error message to use if an exception is thrown.</param>
    /// <exception cref="MiniaudioException">Thrown when the result code is not MA_SUCCESS.</exception>
    internal static void ThrowIfError(MaResult result, string message)
    {
        if (result != MaResult.Success)
        {
            throw new MiniaudioException(message, result);
        }
    }
}
