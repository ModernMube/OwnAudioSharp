using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OwnaudioLegacy.Utilities
{
    internal static class DoubleUtil
    {
        internal const double DBL_EPSILON = 1.1102230246251567e-016;

        /// <summary>
        /// AreClose - Returns whether or not two doubles are "close".  That is, whether or
        /// not they are within epsilon of each other.  Note that this epsilon is proportional
        /// to the numbers themselves to that AreClose survives scalar multiplication.
        /// There are plenty of ways for this to return false even for numbers which
        /// are theoretically identical, so no code calling this should fail to work if this
        /// returns false.  This is important enough to repeat:
        /// NB: NO CODE CALLING THIS FUNCTION SHOULD DEPEND ON ACCURATE RESULTS - this should be
        /// used for optimizations *only*.
        /// </summary>
        /// <returns>
        /// bool - the result of the AreClose comparison.
        /// </returns>
        /// <param name="value1">The first double to compare.</param>
        /// <param name="value2">The second double to compare.</param>
        public static bool AreClose(double value1, double value2)
        {
            if (value1 == value2) // in case they are Infinities (then epsilon check does not work)
                { return true; }

            // This computes (|value1-value2| / (|value1| + |value2| + 10.0)) < DBL_EPSILON
            double eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DBL_EPSILON;
            double delta = value1 - value2;
            return -eps < delta && eps > delta;
        }
    }
}
