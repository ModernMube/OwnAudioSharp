using System;

namespace Ownaudio.Safe.Validation;

/// <summary>
/// Tiny argument checks for the public entry points of this layer.
/// Everything below assumes the values already went through here.
/// </summary>
internal static class Guard
{
    // closed interval, both ends included
    internal static void InRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {min} and {max}.");
    }

    internal static void NotNull<T>(T value, string paramName) where T : class
    {
        if (value is null) throw new ArgumentNullException(paramName);
    }

    internal static void NotDisposed(bool isDisposed, string objectName)
    {
        if(isDisposed) throw new ObjectDisposedException(objectName);
    }
}
