using System;

namespace OwnaudioLegacy.Exceptions;

/// <summary>
/// An exception that is thrown when an error occured during Bufdio-specific operations.
/// <para>Implements: <see cref="Exception"/>.</para>
/// </summary>
[Obsolete("This is legacy code, available only for compatibility!")]
public class OwnaudioException : Exception
{
    /// <summary>
    /// Initializes <see cref="OwnaudioException"/>.
    /// </summary>
    public OwnaudioException()
    {
    }

    /// <summary>
    /// Initializes <see cref="OwnaudioException"/> by specifying exception message.
    /// </summary>
    /// <param name="message">A <c>string</c> represents exception message.</param>
    public OwnaudioException(string message) : base(message)
    {
    }
}
