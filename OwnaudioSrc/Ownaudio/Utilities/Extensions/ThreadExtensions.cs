using System;
using System.Threading;

namespace Ownaudio.Utilities.Extensions;

/// <summary>
/// Additional method of threads
/// </summary>
internal static class ThreadExtensions
{
    /// <summary>
    /// Checking thread completion
    /// </summary>
    /// <param name="thread"></param>
    /// <param name="breaker"></param>
    public static void EnsureThreadDone(this Thread thread, Func<bool>? breaker = default)
    {
        while (thread.IsAlive)
        {
            if (breaker != null && breaker())
            {
                break;
            }

            Thread.Sleep(10);
        }
    }
}
