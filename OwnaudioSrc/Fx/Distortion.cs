using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Distortion effect with overdrive and soft clipping
    /// </summary>
    public class Distortion : SampleProcessorBase
    {
        private float _drive = 1.0f;
        private float _mix = 1.0f;
        private float _outputGain = 0.5f;

        /// <summary>
        /// Drive amount (1.0 - 10.0). Higher values create more distortion.
        /// </summary>
        public float Drive
        {
            get => _drive;
            set => _drive = Math.Clamp(value, 1.0f, 10.0f);
        }

        /// <summary>
        /// Mix between dry and wet signal (0.0 - 1.0).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Output gain compensation (0.1 - 1.0).
        /// </summary>
        public float OutputGain
        {
            get => _outputGain;
            set => _outputGain = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Initialize Distortion Processor.
        /// </summary>
        /// <param name="drive">Drive amount (1.0 - 10.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="outputGain">Output gain (0.1 - 1.0)</param>
        public Distortion(float drive = 2.0f, float mix = 1.0f, float outputGain = 0.5f)
        {
            Drive = drive;
            Mix = mix;
            OutputGain = outputGain;
        }

        /// <summary>
        /// Process samples with distortion effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float driven = input * Drive;

                float distorted = SoftClip(driven);

                distorted *= OutputGain;

                samples[i] = (input * (1.0f - Mix)) + (distorted * Mix);
            }
        }

        /// <summary>
        /// Reset distortion effect (no internal state to clear).
        /// </summary>
        public override void Reset()
        {
            // No internal state to reset
        }

        /// <summary>
        /// Soft clipping function for smooth distortion
        /// </summary>
        private static float SoftClip(float input)
        {
            if (Math.Abs(input) <= 1.0f)
                return input;

            return Math.Sign(input) * (2.0f - 2.0f / (Math.Abs(input) + 1.0f));
        }
    }
}
