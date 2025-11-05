using System;
using System.Diagnostics;

namespace OwnaudioLegacy.Utilities;

/// <summary>
/// Verification of the specified conditions
/// </summary>
[DebuggerStepThrough]
internal static class Ensure
{
    /// <summary>
    /// Condition check
    /// </summary>
    /// <typeparam name="TException"></typeparam>
    /// <param name="condition">condition</param>
    /// <param name="message">message text</param>
    public static void That<TException>(bool condition, string? message = null) where TException : Exception
    {
        #nullable disable
        if (!condition)
        {
            throw string.IsNullOrEmpty(message)
                ? Activator.CreateInstance<TException>()
                : (TException)Activator.CreateInstance(typeof(TException), message);
        }
        #nullable restore
    }

    /// <summary>
    /// Null value check
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="argument"></param>
    /// <param name="name"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public static void NotNull<T>(T argument, string name) where T : class
    {
        if (argument == null)
        {
            throw new ArgumentNullException(name);
        }
    }
}
