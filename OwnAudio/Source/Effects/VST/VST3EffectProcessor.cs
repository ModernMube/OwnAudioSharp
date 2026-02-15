using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnVST3Host;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// VST3 plugin wrapper implementing IEffectProcessor for seamless integration
    /// with the OwnAudioSharp effect chain.
    /// </summary>
    public sealed class VST3EffectProcessor : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;
        private float _mix;

        private readonly OwnVst3Wrapper _vst3;
        private readonly string _pluginPath;
        private bool _vst3Initialized;

        private float[][]? _planarInputBuffers;
        private float[][]? _planarOutputBuffers;
        private float[]? _dryBuffer;
        private int _allocatedBlockSize;

        /// <summary>
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of this effect instance.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? _vst3?.Name ?? "VST3 Effect";
        }

        /// <summary>
        /// Gets or sets whether this effect is enabled.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Gets or sets the wet/dry mix (0.0 = dry, 1.0 = wet).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Gets the vendor/manufacturer of the loaded VST3 plugin.
        /// </summary>
        public string Vendor => _vst3?.Vendor ?? string.Empty;

        /// <summary>
        /// Gets whether the loaded plugin is an audio effect.
        /// </summary>
        public bool IsEffect => _vst3?.IsEffect ?? false;

        /// <summary>
        /// Gets whether the loaded plugin is an instrument.
        /// </summary>
        public bool IsInstrument => _vst3?.IsInstrument ?? false;

        /// <summary>
        /// Gets the full path to the loaded VST3 plugin file.
        /// </summary>
        public string PluginPath => _pluginPath;

        /// <summary>
        /// Gets the number of exposed parameters.
        /// </summary>
        public int ParameterCount => _vst3?.GetParameterCount() ?? 0;

        /// <summary>
        /// Gets whether the plugin has a GUI editor.
        /// </summary>
        public bool HasEditor => _vst3?.GetEditorSize() != null;

        /// <summary>
        /// Creates a VST3 effect processor with a pre-loaded wrapper.
        /// This constructor is intended to be called by VST3PluginHost.
        /// </summary>
        /// <param name="wrapper">Pre-loaded VST3 wrapper instance (managed by VST3PluginHost).</param>
        /// <exception cref="ArgumentNullException">When wrapper is null.</exception>
        /// <remarks>
        /// IMPORTANT: This processor does NOT own the wrapper and will NOT dispose it.
        /// The wrapper lifecycle is managed by the VST3PluginHost that created this processor.
        /// </remarks>
        internal VST3EffectProcessor(OwnVst3Wrapper wrapper)
        {
            _vst3 = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
            _id = Guid.NewGuid();
            _enabled = true;
            _mix = 1.0f;
            _pluginPath = string.Empty; // Path is managed by the host
            _name = _vst3.Name ?? "VST3 Effect";
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _vst3.Initialize(config.SampleRate, config.BufferSize);
            _vst3Initialized = true;

            AllocateBuffers(config.BufferSize, config.Channels);
        }

        /// <summary>
        /// Processes the audio buffer through the VST3 plugin.
        /// </summary>
        /// <param name="buffer">The interleaved stereo audio buffer to process.</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled || !_vst3Initialized || _disposed)
                return;

            int channels = _config.Channels;
            int totalSamples = frameCount * channels;

            if (frameCount > _allocatedBlockSize)
            {
                AllocateBuffers(frameCount, channels);
            }

            bool needsDryMix = _mix < 0.999f;
            if (needsDryMix)
            {
                buffer.Slice(0, totalSamples).CopyTo(_dryBuffer.AsSpan());
            }

            try
            {
                VST3BufferConverter.InterleavedToPlanar(buffer, _planarInputBuffers!, channels, frameCount);

                _vst3.ProcessAudio(_planarInputBuffers!, _planarOutputBuffers!, channels, frameCount);

                VST3BufferConverter.PlanarToInterleaved(_planarOutputBuffers!, buffer, channels, frameCount);
            }
            catch
            {
                // Ignore processing errors during editor operations or plugin instability
                // This prevents crashes when the plugin's UI and audio threads are temporarily out of sync
                // If error occurs, the buffer retains its input state (bypass behavior)
            }

            if (needsDryMix)
            {
                float wet = _mix;
                float dry = 1.0f - wet;
                for (int i = 0; i < totalSamples; i++)
                {
                    buffer[i] = _dryBuffer![i] * dry + buffer[i] * wet;
                }
            }
        }

        /// <summary>
        /// Resets the effect state.
        /// </summary>
        public void Reset()
        {
            if (_planarInputBuffers != null)
            {
                for (int ch = 0; ch < _planarInputBuffers.Length; ch++)
                {
                    Array.Clear(_planarInputBuffers[ch], 0, _planarInputBuffers[ch].Length);
                    Array.Clear(_planarOutputBuffers![ch], 0, _planarOutputBuffers[ch].Length);
                }
            }
            if (_dryBuffer != null)
            {
                Array.Clear(_dryBuffer, 0, _dryBuffer.Length);
            }

            if (_vst3Initialized && _config != null)
            {
                _vst3.Initialize(_config.SampleRate, _config.BufferSize);
            }
        }

        /// <summary>
        /// Gets all parameter information from the VST3 plugin.
        /// </summary>
        /// <returns>Array of parameter information.</returns>
        public VST3ParameterInfo[] GetParameters()
        {
            if (_vst3 == null) return Array.Empty<VST3ParameterInfo>();

            var vst3Params = _vst3.GetAllParameters();
            var result = new VST3ParameterInfo[vst3Params.Count];

            for (int i = 0; i < vst3Params.Count; i++)
            {
                var p = vst3Params[i];
                result[i] = new VST3ParameterInfo(
                    (uint)p.Id,
                    p.Name,
                    p.CurrentValue,
                    p.MinValue,
                    p.MaxValue,
                    p.DefaultValue
                );
            }

            return result;
        }

        /// <summary>
        /// Sets a parameter value by ID.
        /// </summary>
        /// <param name="id">Parameter ID.</param>
        /// <param name="value">Normalized value (typically 0.0 to 1.0).</param>
        public void SetParameter(int id, double value)
        {
            _vst3?.SetParameter(id, value);
        }

        /// <summary>
        /// Gets a parameter value by ID.
        /// </summary>
        /// <param name="id">Parameter ID.</param>
        /// <returns>Current parameter value.</returns>
        public double GetParameter(int id)
        {
            return _vst3?.GetParameter(id) ?? 0.0;
        }

        /// <summary>
        /// Gets the preferred editor window size.
        /// </summary>
        /// <returns>Width and height tuple, or null if no editor available.</returns>
        public (int Width, int Height)? GetEditorSize()
        {
            var size = _vst3?.GetEditorSize();
            if (size == null) return null;
            return (size.Value.Width, size.Value.Height);
        }

        /// <summary>
        /// Allocates internal buffers for audio processing.
        /// </summary>
        private void AllocateBuffers(int blockSize, int channels)
        {
            _planarInputBuffers = new float[channels][];
            _planarOutputBuffers = new float[channels][];

            for (int ch = 0; ch < channels; ch++)
            {
                _planarInputBuffers[ch] = new float[blockSize];
                _planarOutputBuffers[ch] = new float[blockSize];
            }

            _dryBuffer = new float[blockSize * channels];
            _allocatedBlockSize = blockSize;
        }

        /// <summary>
        /// Disposes the effect and releases resources.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: This does NOT dispose the underlying VST3 wrapper,
        /// as it is owned and managed by the VST3PluginHost.
        /// Only internal buffers are released.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;

            // Only clean up our own buffers, NOT the wrapper (owned by VST3PluginHost)
            _planarInputBuffers = null;
            _planarOutputBuffers = null;
            _dryBuffer = null;
            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        public override string ToString()
        {
            return $"VST3: {_name} ({Vendor}), Enabled={_enabled}, Mix={_mix:F2}";
        }
    }
}
