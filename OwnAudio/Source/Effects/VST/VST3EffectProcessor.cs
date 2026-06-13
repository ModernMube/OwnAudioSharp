using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnVST3Host;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// VST3 plugin wrapper implementing IEffectProcessor for seamless integration
    /// with the OwnAudioSharp effect chain.
    ///
    /// Threading model:
    ///   Audio thread – Process() is called here. ThreadedVst3Wrapper.ProcessAudio() drains
    ///                  the lock-free SPSC queue (parameter/tempo/transport changes posted by
    ///                  the UI thread) before each block – zero extra allocations.
    ///   UI thread    – SetParameter / SetTempo / SetTransportPlaying / ResetPosition enqueue
    ///                  lock-free; they return immediately without blocking the UI.
    ///   Any thread   – IsReady, Enabled, Mix reads/writes are volatile-safe.
    ///
    /// Ownership:
    ///   This processor does NOT own the ThreadedVst3Wrapper. The owning VST3PluginHost
    ///   manages its lifetime. Always dispose the host AFTER the audio engine is stopped.
    ///
    /// Required usage:
    ///   1. await host.InitializeAudioAsync(sampleRate, blockSize)
    ///   2. var proc = host.GetProcessor()        // only when host.IsReady
    ///   3. mixer.AddMasterEffect(proc)            // Initialize() validates IsReady
    ///   4. mixer.Stop()                           // calls Reset() → transport stopped
    ///   5. proc.Dispose(); host.Dispose()
    /// </summary>
    public sealed class VST3EffectProcessor : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private volatile bool _enabled;
        private volatile bool _disposed;
        private AudioConfig? _config;
        private float _mix;

        private readonly ThreadedVst3Wrapper _threaded;

        private bool _buffersAllocated;

        private float[][]? _planarInputBuffers;
        private float[][]? _planarOutputBuffers;
        private float[]? _dryBuffer;
        private int _allocatedBlockSize;

        #region IEffectProcessor properties

        /// <inheritdoc/>
        public Guid Id => _id;

        /// <inheritdoc/>
        public string Name
        {
            get => _name;
            set => _name = value ?? "VST3 Effect";
        }

        /// <inheritdoc/>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <inheritdoc/>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Returns true when the underlying VST3 plugin is in Ready or Processing state
        /// and this processor has not been disposed.
        /// Safe to read from any thread (volatile).
        /// </summary>
        public bool IsReady => !_disposed && _threaded.IsReady;

        /// <summary>
        /// Gets the processing latency introduced by the loaded VST3 plugin in samples.
        /// </summary>
        /// <remarks>
        /// Returns 0 until the native <c>OwnVst3Wrapper</c> exposes
        /// <c>IAudioProcessor::getLatencySamples()</c> through its managed wrapper.
        /// Once the native layer is updated, this property will read the value from
        /// <c>_threaded.InnerWrapper.LatencySamples</c> after <c>setupProcessing()</c>
        /// completes. The property is safe to read from any thread.
        /// </remarks>
        public int LatencySamples => 0;

        #endregion

        #region VST-specific read-only info

        /// <summary>Gets the vendor of the loaded VST3 plugin.</summary>
        public string Vendor => _threaded.InnerWrapper?.Vendor ?? string.Empty;

        /// <summary>Gets whether the loaded plugin is an audio effect.</summary>
        public bool IsEffect => _threaded.InnerWrapper?.IsEffect ?? false;

        /// <summary>Gets whether the loaded plugin is an instrument.</summary>
        public bool IsInstrument => _threaded.InnerWrapper?.IsInstrument ?? false;
        
        #endregion

        internal VST3EffectProcessor(ThreadedVst3Wrapper threaded)
        {
            _threaded = threaded ?? throw new ArgumentNullException(nameof(threaded));
            _id       = Guid.NewGuid();
            _enabled  = true;
            _mix      = 1.0f;
            _name     = _threaded.InnerWrapper?.Name ?? "VST3 Effect";
        }

        #region IEffectProcessor – Initialize
        
        /// <summary>
        /// Stores the audio configuration and allocates processing buffers.
        /// Does NOT call InitializeAsync – the plugin must already be in Ready state
        /// (call VST3PluginHost.InitializeAudioAsync first).
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the plugin is not in Ready state.
        /// </exception>
        public void Initialize(AudioConfig config)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VST3EffectProcessor));

            if (!_threaded.IsReady)
                throw new InvalidOperationException(
                    $"VST3 plugin '{_name}' is not audio-initialized. " +
                    $"Call and await VST3PluginHost.InitializeAudioAsync(sampleRate, blockSize) " +
                    $"before adding this processor to an effect chain. " +
                    $"Current state: {_threaded.State}");

            _config = config ?? throw new ArgumentNullException(nameof(config));

            int effectiveChannels = _threaded.InnerWrapper != null && _threaded.InnerWrapper.ActualOutputChannels > 0
                ? _threaded.InnerWrapper.ActualOutputChannels
                : config.Channels;

            AllocateBuffers(config.BufferSize, effectiveChannels);
            _buffersAllocated = true;
        }
        
        #endregion

        #region IEffectProcessor – Process (audio thread)
        
        /// <summary>
        /// Processes the audio buffer through the VST3 plugin.
        /// Called from the audio thread. Returns immediately (pass-through) when
        /// the plugin is not ready, disabled, or disposed.
        /// The ThreadedVst3Wrapper drains the UI→audio SPSC queue before each block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            // Fast path: skip when not usable
            if (_disposed || !_enabled || !_buffersAllocated || !_threaded.IsReady)
                return;

            int channels     = _config!.Channels;
            int totalSamples = frameCount * channels;

            if (frameCount > _allocatedBlockSize)
                AllocateBuffers(frameCount, channels);

            bool needsDryMix = _mix < 0.999f;
            if (needsDryMix)
                buffer.Slice(0, totalSamples).CopyTo(_dryBuffer.AsSpan());

            try
            {
                VST3BufferConverter.InterleavedToPlanar(buffer, _planarInputBuffers!, channels, frameCount);

                _threaded.ProcessAudio(_planarInputBuffers!, _planarOutputBuffers!, channels, frameCount);

                VST3BufferConverter.PlanarToInterleaved(_planarOutputBuffers!, buffer, channels, frameCount);
            }
            catch {}

            if (needsDryMix)
            {
                float wet = _mix;
                float dry = 1.0f - wet;
                for (int i = 0; i < totalSamples; i++)
                    buffer[i] = _dryBuffer![i] * dry + buffer[i] * wet;
            }
        }
        
        #endregion

       #region IEffectProcessor – Reset
       
        /// <summary>
        /// Clears internal audio buffers and stops the VST3 transport.
        /// Does NOT re-initialize the plugin (which would be blocking and expensive).
        /// Called automatically by SourceWithEffects.Stop() / AudioMixer.Stop().
        /// </summary>
        public void Reset()
        {
            if (_disposed) return;

            _threaded.SetTransportState(false);
            _threaded.ResetTransportPosition();

            if (_planarInputBuffers != null)
            {
                for (int ch = 0; ch < _planarInputBuffers.Length; ch++)
                {
                    Array.Clear(_planarInputBuffers[ch], 0, _planarInputBuffers[ch].Length);
                    if (_planarOutputBuffers != null)
                        Array.Clear(_planarOutputBuffers[ch], 0, _planarOutputBuffers[ch].Length);
                }
            }

            if (_dryBuffer != null)
                Array.Clear(_dryBuffer, 0, _dryBuffer.Length);
        }
        
        #endregion

        #region VST-specific transport / parameter helpers
        
        /// <summary>
        /// Sets the playback tempo. Lock-free enqueue to the audio thread.
        /// Applied before the next ProcessAudio block (~1 block latency).
        /// </summary>
        public void SetTempo(double bpm) => _threaded.SetTempo(bpm);

        /// <summary>
        /// Sets the transport playing state. Lock-free enqueue.
        /// </summary>
        public void SetTransportPlaying(bool playing) => _threaded.SetTransportState(playing);

        /// <summary>
        /// Resets the transport sample position. Lock-free enqueue.
        /// </summary>
        public void ResetPosition() => _threaded.ResetTransportPosition();

        /// <summary>
        /// Sets a parameter value. Lock-free enqueue to the audio thread.
        /// Applied before the next ProcessAudio block (~1 block latency).
        /// </summary>
        public void SetParameter(int id, double value) => _threaded.SetParameter(id, value);

        /// <summary>
        /// Sets multiple parameters synchronously on the dedicated plugin thread.
        /// Unlike SetParameter (SPSC queue, audio thread), this executes on the same thread
        /// used for plugin initialization and GetParameters, so the native controller state
        /// is updated immediately without requiring a processAudio drain cycle.
        /// Use for non-realtime operations such as project state restoration.
        /// </summary>
        public async Task ApplyParametersAsync(IReadOnlyDictionary<int, double> parameters)
        {
            foreach (var kv in parameters)
                await _threaded.SetParameterAsync(kv.Key, kv.Value).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets all parameter information from the VST3 plugin.
        /// Reads via InnerWrapper – only call after plugin is in Ready state.
        /// </summary>
        public VST3ParameterInfo[] GetParameters()
        {
            var vst3Params = _threaded.InnerWrapper.GetAllParameters();
            var result     = new VST3ParameterInfo[vst3Params.Count];

            for (int i = 0; i < vst3Params.Count; i++)
            {
                var p = vst3Params[i];
                result[i] = new VST3ParameterInfo(
                    (uint)p.Id, p.Name, p.CurrentValue, p.MinValue, p.MaxValue, p.DefaultValue);
            }

            return result;
        }

        /// <summary>
        /// Gets the preferred editor window size.
        /// </summary>
        public (int Width, int Height)? GetEditorSize()
        {
            var size = _threaded?.InnerWrapper?.GetEditorSize();
            return size is null ? null : (size.Value.Width, size.Value.Height);
        }
        
        #endregion

        /// <summary>
        /// Releases internal audio buffers and stops the VST3 transport.
        /// Does NOT dispose the ThreadedVst3Wrapper – that is owned by VST3PluginHost.
        /// Call host.Dispose() separately after the audio engine has stopped.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            try
            {
                _threaded.SetTransportState(false);
                _threaded.ResetTransportPosition();
            }
            catch { }

            _planarInputBuffers  = null;
            _planarOutputBuffers = null;
            _dryBuffer           = null;
            _buffersAllocated    = false;
        }

        #region Private helpers
        
        private void AllocateBuffers(int blockSize, int channels)
        {
            _planarInputBuffers  = new float[channels][];
            _planarOutputBuffers = new float[channels][];

            for (int ch = 0; ch < channels; ch++)
            {
                _planarInputBuffers[ch]  = new float[blockSize];
                _planarOutputBuffers[ch] = new float[blockSize];
            }

            _dryBuffer = new float[blockSize * channels];
            _allocatedBlockSize = blockSize;
        }
        
        #endregion

        public override string ToString() =>
            $"VST3: {_name} ({Vendor}), Ready={IsReady}, Enabled={_enabled}, Mix={_mix:F2}";
    }
}
