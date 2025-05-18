using System;
using System.Runtime.InteropServices;
using Ownaudio.Bindings.Miniaudio;
using Ownaudio.Exceptions;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.Utilities.Extensions
{
    /// <summary>
    /// Kiterjesztések a miniaudio eredménykódokhoz.
    /// </summary>
    public static class MaResultExtensions
    {
        /// <summary>
        /// Ellenőrzi a miniaudio művelet eredményét, és kivételt dob, ha hiba történt.
        /// </summary>
        /// <param name="result">A ellenőrizendő miniaudio eredmény (egész számként).</param>
        /// <param name="errorMessage">Az üzenet, amelyet a kivételben kell használni, ha hiba történt.</param>
        /// <returns>Ugyanazt az eredménykódot adja vissza, ha az nem jelent hibát.</returns>
        /// <exception cref="MiniaudioException">Ha az eredmény hibát jelez.</exception>
        public static int MaGuard(this int result, string errorMessage = "Miniaudio hiba történt")
        {
            if (result != 0) // MA_SUCCESS = 0
            {
                throw new MiniaudioException(errorMessage, result);
            }

            return result;
        }

        /// <summary>
        /// Lekéri a miniaudio eredménykód szöveges leírását.
        /// </summary>
        /// <param name="errorCode">A miniaudio eredmény egész számként.</param>
        /// <returns>Az eredménykód szöveges leírása.</returns>
        public static string MaErrorToText(this int errorCode)
        {
            IntPtr errorStringPtr = ma_result_description((MaResult)errorCode);

            if (errorStringPtr == IntPtr.Zero)
                return "Ismeretlen miniaudio hiba";

            return Marshal.PtrToStringAnsi(errorStringPtr) ?? "Ismeretlen miniaudio hiba";
        }

        /// <summary>
        /// Ellenőrzi a miniaudio művelet eredményét, és kivételt dob, ha hiba történt.
        /// </summary>
        /// <param name="result">A ellenőrizendő miniaudio eredmény.</param>
        /// <param name="errorMessage">Az üzenet, amelyet a kivételben kell használni, ha hiba történt.</param>
        /// <returns>Ugyanazt az eredménykódot adja vissza, ha az nem jelent hibát.</returns>
        /// <exception cref="MiniaudioException">Ha az eredmény hibát jelez.</exception>
        internal static MaResult MaGuard(this MaResult result, string errorMessage = "Miniaudio hiba történt")
        {
            if (result != MaResult.Success)
            {
                throw new MiniaudioException(errorMessage, (int)result);
            }

            return result;
        }

        /// <summary>
        /// Lekéri a miniaudio eredménykód szöveges leírását.
        /// </summary>
        /// <param name="result">A miniaudio eredmény.</param>
        /// <returns>Az eredménykód szöveges leírása.</returns>
        internal static string MaErrorToText(this MaResult result)
        {
            IntPtr errorStringPtr = ma_result_description(result);

            if (errorStringPtr == IntPtr.Zero)
                return "Ismeretlen miniaudio hiba";

            return Marshal.PtrToStringAnsi(errorStringPtr) ?? "Ismeretlen miniaudio hiba";
        }


    }
}
