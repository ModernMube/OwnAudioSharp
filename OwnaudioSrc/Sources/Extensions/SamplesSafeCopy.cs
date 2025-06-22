using System;

namespace Ownaudio.Sources.Extensions
{
    public static class SamplesSafeCopy
    {
        public static bool SafeCopyTo<T>(this ReadOnlySpan<T> source, Span<T> destination)
        {
            if (source.Length <= destination.Length)
            {
                source.CopyTo(destination);
                return true;
            }
            return false;
        }

        public static bool SafeCopyTo<T>(this Span<T> source, Span<T> destination)
        {
            if (source.Length <= destination.Length)
            {
                source.CopyTo(destination);
                return true;
            }
            return false;
        }
    }
}
