using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Phase and time alignment - delay and phase inversion between channels
    /// </summary>
    public class PhaseAlignment
    {
        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        private float _sampleRate;
        
        /// <summary>
        /// Time delays in milliseconds for L, R, and Sub channels.
        /// </summary>
        private float[] _delays = new float[3];
        
        /// <summary>
        /// Phase inversion flags for L, R, and Sub channels.
        /// </summary>
        private bool[] _phaseInvert = new bool[3];
        
        /// <summary>
        /// Delay buffers for each channel.
        /// </summary>
        private float[][] _delayBuffers = new float[3][];
        
        /// <summary>
        /// Current write indices for delay buffers.
        /// </summary>
        private int[] _delayBufferIndices = new int[3];
        
        /// <summary>
        /// Delay lengths in samples for each channel.
        /// </summary>
        private int[] _delaySamples = new int[3];
        
        /// <summary>
        /// Initializes a new instance of the <see cref="PhaseAlignment"/> class.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        public PhaseAlignment(float sampleRate)
        {
            _sampleRate = sampleRate;
            
            int maxDelaySamples = (int)(sampleRate * 0.05f);
            for (int i = 0; i < 3; i++)
            {
                _delayBuffers[i] = new float[maxDelaySamples];
            }
        }
        
        /// <summary>
        /// Sets the time delays for all channels.
        /// </summary>
        /// <param name="delays">Array of delays in milliseconds for L, R, and Sub channels.</param>
        public void SetDelays(float[] delays)
        {
            if (delays.Length >= 3)
            {
                // Check if delays actually changed
                bool changed = false;
                for (int i = 0; i < 3; i++)
                {
                    if (Math.Abs(_delays[i] - delays[i]) > 0.01f)
                    {
                        changed = true;
                        break;
                    }
                }
                
                if (changed)
                {
                    Array.Copy(delays, _delays, 3);
                    
                    // Calculate delay samples
                    for (int i = 0; i < 3; i++)
                    {
                        _delaySamples[i] = (int)(_delays[i] * _sampleRate / 1000.0f);
                        _delaySamples[i] = Math.Clamp(_delaySamples[i], 0, _delayBuffers[i].Length - 1);
                    }
                    
                    Reset(); // Clear delay buffers when delay values change
                }
            }
        }
        
        /// <summary>
        /// Sets the phase inversion flags for all channels.
        /// </summary>
        /// <param name="inversions">Array of phase inversion flags for L, R, and Sub channels.</param>
        public void SetPhaseInversions(bool[] inversions)
        {
            if (inversions.Length >= 3)
            {
                Array.Copy(inversions, _phaseInvert, 3);
            }
        }
        
        /// <summary>
        /// Processes audio through phase alignment (delay and phase inversion).
        /// Optimized: Removed modulo operator for better performance.
        /// </summary>
        /// <param name="buffer">Audio buffer to process.</param>
        /// <param name="channel">Channel index (0 = L, 1 = R, 2 = Sub).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        public void Process(Span<float> buffer, int channel, int frameCount)
        {
            if (channel < 0 || channel >= 3)
                return;
            
            int delaySamples = _delaySamples[channel];
            bool invert = _phaseInvert[channel];
            
            if (delaySamples == 0 && !invert)
                return; // Nothing to do
            
            float[] delayBuffer = _delayBuffers[channel];
            int writeIndex = _delayBufferIndices[channel];
            int bufferSize = delayBuffer.Length;
            
            for (int i = 0; i < buffer.Length; i++)
            {
                float input = buffer[i];
                
                // Write to delay buffer
                delayBuffer[writeIndex] = input;
                
                // Read from delay buffer (optimized: avoid modulo)
                int readIndex = writeIndex - delaySamples;
                if (readIndex < 0) readIndex += bufferSize;
                
                float output = delayBuffer[readIndex];
                
                // Phase inversion
                if (invert)
                    output = -output;
                
                buffer[i] = output;
                
                // Update index (optimized: avoid modulo)
                writeIndex++;
                if (writeIndex >= bufferSize) writeIndex = 0;
            }
            
            _delayBufferIndices[channel] = writeIndex;
        }
        
        /// <summary>
        /// Resets all delay buffers to zero.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < 3; i++)
            {
                Array.Clear(_delayBuffers[i]);
                _delayBufferIndices[i] = 0;
            }
        }
    }
}
