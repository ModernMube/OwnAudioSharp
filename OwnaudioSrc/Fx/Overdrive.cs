using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Overdrive effect with tube-like saturation
    /// </summary>
    public class Overdrive : SampleProcessorBase
    {
        private float _gain = 2.0f;
        private float _tone = 0.5f;
        private float _mix = 1.0f;
        private float _outputLevel = 0.7f;

        // Tone control filters
        private float _lowPassState = 0.0f;
        private float _highPassState = 0.0f;

        /// <summary>
        /// Input gain (1.0 - 5.0). Controls the amount of overdrive.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 1.0f, 5.0f);
        }

        /// <summary>
        /// Tone control (0.0 - 1.0). 0.0 = dark, 1.0 = bright.
        /// </summary>
        public float Tone
        {
            get => _tone;
            set => _tone = Math.Clamp(value, 0.0f, 1.0f);
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
        /// Output level (0.1 - 1.0).
        /// </summary>
        public float OutputLevel
        {
            get => _outputLevel;
            set => _outputLevel = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Initialize Overdrive Processor.
        /// </summary>
        /// <param name="gain">Input gain (1.0 - 5.0)</param>
        /// <param name="tone">Tone control (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="outputLevel">Output level (0.1 - 1.0)</param>
        public Overdrive(float gain = 2.0f, float tone = 0.5f, float mix = 1.0f, float outputLevel = 0.7f)
        {
            Gain = gain;
            Tone = tone;
            Mix = mix;
            OutputLevel = outputLevel;
        }

        /// <summary>
        /// Process samples with overdrive effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float gained = input * Gain;

                float overdriven = TubeSaturation(gained);

                overdriven = ApplyToneControl(overdriven);

                overdriven *= OutputLevel;

                samples[i] = (input * (1.0f - Mix)) + (overdriven * Mix);
            }
        }

        /// <summary>
        /// Reset overdrive effect state.
        /// </summary>
        public override void Reset()
        {
            _lowPassState = 0.0f;
            _highPassState = 0.0f;
        }

        /// <summary>
        /// Tube-like asymmetric saturation
        /// </summary>
        private static float TubeSaturation(float input)
        {
            if (input >= 0)
            {
                return (float)(Math.Tanh(input * 0.7) * 1.2);
            }
            else
            {
                return (float)(Math.Tanh(input * 0.9) * 0.9);
            }
        }

        /// <summary>
        /// Simple tone control using low-pass and high-pass filtering
        /// </summary>
        private float ApplyToneControl(float input)
        {
            float lowPassCutoff = 0.1f + (Tone * 0.4f);
            float highPassCutoff = 0.05f + ((1.0f - Tone) * 0.2f);

            _lowPassState += lowPassCutoff * (input - _lowPassState);
            _highPassState += highPassCutoff * (input - _highPassState);

            return _lowPassState - _highPassState * (1.0f - Tone);
        }
    }
}
