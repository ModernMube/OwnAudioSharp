using System;

namespace Ownaudio.Safe.Validation;

/// <summary>
/// Lightweight input-validation helpers used at every public entry point in this layer.
/// All validation must happen before any native call — the native side assumes valid inputs.
/// </summary>
internal static class Guard
{
    #region Range and null checks

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is
    /// outside the closed interval [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Inclusive upper bound.</param>
    /// <param name="paramName">Parameter name shown in the exception.</param>
    internal static void InRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Value must be between {min} and {max}.");
        }
    }

    /// <summary>
    /// Throws <see cref="ArgumentNullException"/> if <paramref name="value"/> is <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">Any reference type.</typeparam>
    /// <param name="value">The value to check.</param>
    /// <param name="paramName">Parameter name shown in the exception.</param>
    internal static void NotNull<T>(T value, string paramName) where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if <paramref name="isDisposed"/> is
    /// <see langword="true"/>.
    /// </summary>
    /// <param name="isDisposed">Disposal flag of the owning object.</param>
    /// <param name="objectName">Name of the object shown in the exception.</param>
    internal static void NotDisposed(bool isDisposed, string objectName)
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(objectName);
        }
    }

    #endregion
}
