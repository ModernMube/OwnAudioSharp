// License :
//
// SoundTouch audio processing library
// Copyright (c) Olli Parviainen
// C# port Copyright (c) Olaf Woudenberg
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

namespace SoundTouch
{
    using System;
    using System.Diagnostics;

    //using JetBrains.Annotations;

    /// <summary>
    /// <para>Peak detection routine.</para>
    /// <para>
    /// The routine detects highest value on an array of values and calculates the
    /// precise peak location as a mass-center of the 'hump' around the peak value.
    /// </para>
    /// </summary>
    internal sealed class PeakFinder
    {
        // Min, max allowed peak positions within the data vector
        private int _minPosition;
        private int _maxPosition;

        /// <summary>
        /// Initializes a new instance of the <see cref="PeakFinder"/> class.
        /// </summary>
        public PeakFinder()
        {
            _minPosition = _maxPosition = 0;
        }

        /// <summary>
        /// Detect exact peak position of the data vector by finding the largest peak 'hump'
        /// and calculating the mass-center location of the peak hump.
        /// </summary>
        /// <param name="data">Data vector to be analyzed. The data vector has to be at least <paramref name="maxPos"/> items long.</param>
        /// <param name="minPos">Min allowed peak location within the vector data.</param>
        /// <param name="maxPos">Max allowed peak location within the vector data.</param>
        /// <returns>The location of the largest base harmonic peak hump.</returns>
        public double DetectPeak(in ReadOnlySpan<float> data, int minPos, int maxPos)
        {
            int i;

            _minPosition = minPos;
            _maxPosition = maxPos;

            // find absolute peak
            var peakPosition = minPos;
            double peak = data[minPos];
            for (i = minPos + 1; i < maxPos; i++)
            {
                if (data[i] > peak)
                {
                    peak = data[i];
                    peakPosition = i;
                }
            }

            // Calculate exact location of the highest peak mass center
            var highPeak = GetPeakCenter(data, peakPosition);
            peak = highPeak;

            // Now check if the highest peak were in fact harmonic of the true base beat peak
            // - sometimes the highest peak can be Nth harmonic of the true base peak yet
            // just a slightly higher than the true base.
            for (i = 1; i < 3; i++)
            {
                var harmonic = Math.Pow(2.0, i);
                peakPosition = (int)((highPeak / harmonic) + 0.5f);
                if (peakPosition < minPos)
                    break;
                peakPosition = FindTop(data, peakPosition);   // seek true local maximum index
                if (peakPosition == 0)
                    continue;         // no local max here

                // calculate mass-center of possible harmonic peak
                var peakTemp = GetPeakCenter(data, peakPosition);

                // accept harmonic peak if
                // (a) it is found
                // (b) is within ±4% of the expected harmonic interval
                // (c) has at least half x-corr value of the max. peak
                double diff = harmonic * peakTemp / highPeak;
                if ((diff < 0.96) || (diff > 1.04))
                    continue;   // peak too afar from expected

                // now compare to highest detected peak
                var i1 = (int)(highPeak + 0.5);
                var i2 = (int)(peakTemp + 0.5);
                if (data[i2] >= 0.4 * data[i1])
                {
                    // The harmonic is at least half as high primary peak,
                    // thus use the harmonic peak instead
                    peak = peakTemp;
                }
            }

            return peak;
        }

        /// <summary>
        /// Calculates the mass center between given vector items.
        /// </summary>
        private static double CalcMassCenter(in ReadOnlySpan<float> data, int firstPos, int lastPos)
        {
            int i;

            float sum = 0;
            float wsum = 0;
            for (i = firstPos; i <= lastPos; i++)
            {
                sum += i * data[i];
                wsum += data[i];
            }

            if (wsum < 1e-6)
                return 0;

            return sum / wsum;
        }

        /// <summary>
        /// Finds the data vector index where the monotonously decreasing signal crosses the
        /// given level.
        /// </summary>
        /// <param name="data">Data vector.</param>
        /// <param name="level">Goal crossing level.</param>
        /// <param name="peakPosition">Peak position index within the data vectors.</param>
        /// <param name="direction">Direction where to proceed from the peak: 1 = right, -1 = left.</param>
        private int FindCrossingLevel(in ReadOnlySpan<float> data, float level, int peakPosition, int direction)
        {
            var peakLevel = data[peakPosition];
            Debug.Assert(peakLevel >= level, "peakLevel >= level");

            var position = peakPosition;
            while ((position >= _minPosition) && (position + direction < _maxPosition))
            {
                if (data[position + direction] < level)
                    return position;   // crossing found
                position += direction;
            }

            return -1;  // not found
        }

        /// <summary>
        /// Finds real 'top' of a peak hump from neighborhood of the given <paramref name="peakPosition"/>.
        /// </summary>
        /// <param name="data">Data vector.</param>
        /// <param name="peakPosition">Peak position index within the data vectors.</param>
        private int FindTop(in ReadOnlySpan<float> data, int peakPosition)
        {
            var referenceValue = data[peakPosition];

            // seek within ±10 points
            var start = peakPosition - 10;
            if (start < _minPosition)
                start = _minPosition;
            var end = peakPosition + 10;
            if (end > _maxPosition)
                end = _maxPosition;

            for (int i = start; i <= end; i++)
            {
                if (data[i] > referenceValue)
                {
                    peakPosition = i;
                    referenceValue = data[i];
                }
            }

            // failure if max value is at edges of seek range => it's not peak, it's at slope.
            if ((peakPosition == start) || (peakPosition == end))
                return 0;

            return peakPosition;
        }

        /// <summary>
        /// Finds the 'ground' level, i.e. smallest level between two neighboring peaks, to right-
        /// or left-hand side of the given peak position.
        /// </summary>
        /// <param name="data">Data vector.</param>
        /// <param name="peakPosition">Peak position index within the data vector.</param>
        /// <param name="direction">Direction where to proceed from the peak: 1 = right, -1 = left.</param>
        private int FindGround(in ReadOnlySpan<float> data, int peakPosition, int direction)
        {
            var climbCount = 0;
            var referenceValue = data[peakPosition];
            var lowPosition = peakPosition;

            var position = peakPosition;

            while ((position > _minPosition + 1) && (position < _maxPosition - 1))
            {
                var previousPosition = position;
                position += direction;

                // calculate derivative
                var delta = data[position] - data[previousPosition];
                if (delta <= 0)
                {
                    // going downhill, ok
                    if (climbCount != 0)
                    {
                        climbCount--;  // decrease climb count
                    }

                    // check if new minimum found
                    if (data[position] < referenceValue)
                    {
                        // new minimum found
                        lowPosition = position;
                        referenceValue = data[position];
                    }
                }
                else
                {
                    // going uphill, increase climbing counter
                    climbCount++;
                    if (climbCount > 5)
                        break;    // we've been climbing too long => it's next uphill => quit
                }
            }

            return lowPosition;
        }

        /// <summary>
        /// Get exact center of peak near given position by calculating local mass of center.
        /// </summary>
        /// <param name="data">Data vector.</param>
        /// <param name="peakPosition">Peak position index within the data vector.</param>
        private double GetPeakCenter(in ReadOnlySpan<float> data, int peakPosition)
        {
            float cutLevel;             // cutting value

            // find ground positions.
            // bottom positions of the peak 'hump'
            var gp1 = FindGround(data, peakPosition, -1);
            var gp2 = FindGround(data, peakPosition, 1);

            var peakLevel = data[peakPosition];

            if (gp1 == gp2)
            {
                // avoid rounding errors when all are equal
                Debug.Assert(gp1 == peakPosition, "ground is peak position.");
                cutLevel = peakLevel;
            }
            else
            {
                // get average of the ground levels
                // ground level of the peak
                var groundLevel = 0.5f * (data[gp1] + data[gp2]);

                // calculate 70%-level of the peak
                cutLevel = (0.70f * peakLevel) + (0.30f * groundLevel);
            }

            // find mid-level crossings
            // position where the peak 'hump' crosses cutting level
            var crossPosition1 = FindCrossingLevel(data, cutLevel, peakPosition, -1);
            var crossPosition2 = FindCrossingLevel(data, cutLevel, peakPosition, 1);

            if ((crossPosition1 < 0) || (crossPosition2 < 0))
                return 0;   // no crossing, no peak..

            // calculate mass center of the peak surroundings
            return CalcMassCenter(data, crossPosition1, crossPosition2);
        }
    }
}
