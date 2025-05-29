using System;
using System.Runtime.InteropServices;
using Ownaudio.Exceptions;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.Utilities.Extensions
{
    /// <summary>
    /// Extensions to miniaudio result codes.
    /// </summary>
    public static class MaResultExtensions
    {
        /// <summary>
        /// Checks the result of the miniaudio operation and throws an exception if an error occurs.
        /// </summary>
        /// <param name="result">The miniaudio result to check (as an integer).</param>
        /// <param name="errorMessage">The message to use in the exception if an error occurs.</param>
        /// <returns>Returns the same result code if it does not indicate an error.</returns>
        /// <exception cref="MiniaudioException">If the result indicates an error.</exception>
        public static int MaGuard(this int result, string errorMessage = "Miniaudio ERROR: ")
        {
            if (result != 0) // MA_SUCCESS = 0
            {
                throw new MiniaudioException(errorMessage, result);
            }

            return result;
        }

        /// <summary>
        /// Gets a text description of the miniaudio result code.
        /// </summary>
        /// <param name="errorCode">The miniaudio result as an integer.</param>
        /// <returns>A text description of the result code.</returns>
        public static string MaErrorToText(this int errorCode)
        {
            IntPtr errorStringPtr = ma_result_description((MaResult)errorCode);

            if (errorStringPtr == IntPtr.Zero)
                return "Unknown miniaudio error";

            return Marshal.PtrToStringAnsi(errorStringPtr) ?? "Unknown miniaudio error";
        }

        /// <summary>
        /// Checks the result of the miniaudio operation and throws an exception if an error occurs.
        /// </summary>
        /// <param name="result">The miniaudio result to check.</param>
        /// <param name="errorMessage">The message to use in the exception if an error occurs.</param>
        /// <returns>Returns the same result code if it does not indicate an error.</returns>
        /// <exception cref="MiniaudioException">If the result indicates an error.</exception>
        internal static MaResult MaGuard(this MaResult result, string errorMessage = "Miniaudio ERROR: ")
        {
            if (result != MaResult.Success)
            {
                throw new MiniaudioException(errorMessage, (int)result);
            }

            return result;
        }

        /// <summary>
        /// Gets the text description of the miniaudio result code.
        /// </summary>
        /// <param name="result">The miniaudio result.</param>
        /// <returns>The text description of the result code.</returns>
        internal static string MaErrorToText(this MaResult result)
        {
            IntPtr errorStringPtr = ma_result_description(result);

            if (errorStringPtr == IntPtr.Zero)
                return "Unknown miniaudio error";

            return Marshal.PtrToStringAnsi(errorStringPtr) ?? "Unknown miniaudio error";
        }


    }
}
