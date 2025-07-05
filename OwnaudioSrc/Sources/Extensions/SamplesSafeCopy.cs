using System;

namespace Ownaudio.Sources.Extensions
{
    /// <summary>
    /// Provides methods for safely copying elements between spans.
    /// </summary>
    /// <remarks>The <see cref="SamplesSafeCopy"/> class contains extension methods that perform safe copy
    /// operations between spans, ensuring that the destination span has sufficient capacity to hold all elements from
    /// the source span. If the destination span is smaller than the source span, no elements are copied, and the
    /// methods return <see langword="false"/>.</remarks>
    public static class SamplesSafeCopy
    {
        /// <summary>
        /// Copies the elements from the source <see cref="ReadOnlySpan{T}"/> to the destination <see cref="Span{T}"/> 
        /// if the destination has sufficient capacity.
        /// </summary>
        /// <remarks>This method performs a safe copy operation by ensuring that the destination span has
        /// enough capacity  to hold all elements from the source span. If the destination span is smaller than the
        /// source span,  no elements are copied, and the method returns <see langword="false"/>.</remarks>
        /// <typeparam name="T"></typeparam>
        /// <param name="source">The source span containing the elements to copy.</param>
        /// <param name="destination">The destination span where the elements will be copied.</param>
        /// <returns><see langword="true"/> if the elements were successfully copied; otherwise, <see langword="false"/>  if the
        /// destination span does not have sufficient capacity.</returns>
        public static bool SafeCopyTo<T>(this ReadOnlySpan<T> source, Span<T> destination)
        {
            if (source.Length <= destination.Length)
            {
                source.CopyTo(destination);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Copies the elements from the source span to the destination span if the destination span is large enough.
        /// </summary>
        /// <remarks>This method performs a safe copy operation by ensuring that the destination span has
        /// sufficient capacity     to hold all elements from the source span. If the destination span is smaller than
        /// the source span, no     elements are copied, and the method returns <see langword="false"/>.</remarks>
        /// <typeparam name="T"></typeparam>
        /// <param name="source">The span containing the elements to copy.</param>
        /// <param name="destination">The span to which the elements will be copied.</param>
        /// <returns><see langword="true"/> if the elements were successfully copied; otherwise, <see langword="false"/>.</returns>
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
