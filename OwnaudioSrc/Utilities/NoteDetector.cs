using System;
using System.Collections.Generic;
using System.Linq;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// Provides musical note detection functionality from audio signals using onset detection and pitch analysis.
    /// </summary>
    public static class NoteDetector
    {
        /// <summary>
        /// Represents a detected musical note with timing, frequency, and amplitude information.
        /// </summary>
        public struct DetectedNote
        {
            /// <summary>
            /// Gets or sets the sample position where the note starts.
            /// </summary>
            public int StartSample;

            /// <summary>
            /// Gets or sets the sample position where the note ends.
            /// </summary>
            public int EndSample;

            /// <summary>
            /// Gets or sets the fundamental frequency of the detected note in Hz.
            /// </summary>
            public float Frequency;

            /// <summary>
            /// Gets or sets the amplitude (volume) of the detected note.
            /// </summary>
            public float Amplitude;

            /// <summary>
            /// Gets or sets the musical note name (e.g., "A4", "C#3") corresponding to the frequency.
            /// </summary>
            public string NoteName;
        }

        /// <summary>
        /// Detects musical notes from an audio signal by combining onset detection with pitch analysis.
        /// </summary>
        /// <param name="audio">The audio samples as a float array.</param>
        /// <param name="sampleRate">The sample rate of the audio in Hz.</param>
        /// <returns>A list of detected notes with their timing, frequency, and amplitude information.</returns>
        public static List<DetectedNote> DetectNotes(float[] audio, int sampleRate)
        {
            var notes = new List<DetectedNote>();
            int frameSize = 2048;
            int hopSize = 512;

            var onsets = OnsetDetector.DetectOnsets(audio, sampleRate);

            foreach (var onset in onsets)
            {
                if (onset + frameSize < audio.Length)
                {
                    var frame = audio.Skip(onset).Take(frameSize).ToArray();

                    if (IsSound(frame))
                    {
                        float pitch = DetectPitch(frame, sampleRate);
                        if (pitch > 80 && pitch < 2000)
                        {
                            var note = new DetectedNote
                            {
                                StartSample = onset,
                                Frequency = pitch,
                                Amplitude = CalculateRMS(frame),
                                NoteName = FrequencyToNote(pitch)
                            };
                            notes.Add(note);
                        }
                    }
                }
            }

            return notes;
        }

        /// <summary>
        /// Converts a frequency value to its corresponding musical note name and octave.
        /// </summary>
        /// <param name="frequency">The frequency in Hz to convert.</param>
        /// <returns>The musical note name with octave (e.g., "A4", "C#3").</returns>
        private static string FrequencyToNote(float frequency)
        {
            string[] notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int noteNumber = (int)Math.Round(12 * Math.Log2(frequency / 440.0) + 69);
            int octave = noteNumber / 12 - 1;
            int note = noteNumber % 12;
            return notes[note] + octave;
        }

        /// <summary>
        /// Detects the fundamental frequency (pitch) of an audio frame using the YIN algorithm.
        /// </summary>
        /// <param name="buffer">The audio frame to analyze.</param>
        /// <param name="sampleRate">The sample rate of the audio in Hz.</param>
        /// <returns>The detected pitch frequency in Hz, or 0 if no pitch was detected.</returns>
        private static float DetectPitch(float[] buffer, int sampleRate)
        {
            int bufferSize = buffer.Length;
            var yinBuffer = new float[bufferSize / 2];

            for (int tau = 0; tau < yinBuffer.Length; tau++)
            {
                yinBuffer[tau] = 0;
                for (int i = 0; i < yinBuffer.Length; i++)
                {
                    float delta = buffer[i] - buffer[i + tau];
                    yinBuffer[tau] += delta * delta;
                }
            }

            yinBuffer[0] = 1;
            float runningSum = 0;
            for (int tau = 1; tau < yinBuffer.Length; tau++)
            {
                runningSum += yinBuffer[tau];
                yinBuffer[tau] *= tau / runningSum;
            }

            int tauEstimate = -1;
            for (int tau = 2; tau < yinBuffer.Length; tau++)
            {
                if (yinBuffer[tau] < 0.1f && yinBuffer[tau] < yinBuffer[tau - 1])
                {
                    tauEstimate = tau;
                    break;
                }
            }

            return tauEstimate > 0 ? sampleRate / (float)tauEstimate : 0;
        }

        /// <summary>
        /// Calculates the Zero Crossing Rate of an audio frame, which indicates the frequency of sign changes.
        /// </summary>
        /// <param name="frame">The audio frame to analyze.</param>
        /// <returns>The zero crossing rate as a normalized value between 0 and 1.</returns>
        private static float CalculateZCR(float[] frame)
        {
            int crossings = 0;
            for (int i = 1; i < frame.Length; i++)
            {
                if ((frame[i] >= 0) != (frame[i - 1] >= 0))
                    crossings++;
            }
            return crossings / (float)frame.Length;
        }

        /// <summary>
        /// Calculates the Root Mean Square (RMS) amplitude of an audio frame.
        /// </summary>
        /// <param name="frame">The audio frame to analyze.</param>
        /// <returns>The RMS amplitude value representing the average signal strength.</returns>
        private static float CalculateRMS(float[] frame)
        {
            float sum = 0;
            for (int i = 0; i < frame.Length; i++)
            {
                sum += frame[i] * frame[i];
            }
            return (float)Math.Sqrt(sum / frame.Length);
        }

        /// <summary>
        /// Determines whether an audio frame contains a significant sound signal above the noise threshold.
        /// </summary>
        /// <param name="frame">The audio frame to analyze.</param>
        /// <param name="threshold">The minimum RMS threshold to consider as sound. Default is 0.01.</param>
        /// <returns>True if the frame contains sound above the threshold, false otherwise.</returns>
        private static bool IsSound(float[] frame, float threshold = 0.01f)
        {
            return CalculateRMS(frame) > threshold;
        }
    }
}
