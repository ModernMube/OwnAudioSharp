using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnVST3Host;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Wraps a loaded VST3 plugin as an IEffectProcessor so it can sit in the effect chain.
    /// Process() runs on the audio thread, the setters just enqueue lock-free from the UI.
    /// We do not own the ThreadedVst3Wrapper — the VST3PluginHost does, dispose it last.
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
        private int _allocatedChannels;

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
        /// Plugin is up and we are not disposed. Any thread may read it.
        /// </summary>
        public bool IsReady => !_disposed && _threaded.IsReady;

        /// <summary>
        /// Plugin latency in samples per channel, straight from the native host.
        /// The mixer delay-compensates the other tracks with this. Zero before init.
        /// </summary>
        public int LatencySamples => _threaded.InnerWrapper?.LatencySamples ?? 0;

        #endregion

        #region VST-specific read-only info

        /// <summary>
        /// Vendor of the loaded plugin.
        /// </summary>
        public string Vendor => _threaded.InnerWrapper?.Vendor ?? string.Empty;

        /// <summary>
        /// True when the plugin is an audio effect.
        /// </summary>
        public bool IsEffect => _threaded.InnerWrapper?.IsEffect ?? false;

        /// <summary>
        /// True when the plugin is an instrument.
        /// </summary>
        public bool IsInstrument => _threaded.InnerWrapper?.IsInstrument ?? false;

        #endregion

        #region Rust-native hosting

        /// <summary>
        /// Can the Rust chain call the plugin directly? Needs a live handle plus a resolvable
        /// process entry point. If true the managed Process() path is skipped entirely.
        /// </summary>
        internal bool CanHostNatively =>
            !_disposed
            && _threaded.IsReady
            && _threaded.PluginHandle != IntPtr.Zero
            && NativeProcessAudioPointer != IntPtr.Zero;

        /// <summary>
        /// Opaque plugin instance handle for the Rust bridge. Owned by the wrapper.
        /// </summary>
        internal IntPtr NativePluginHandle => _threaded.PluginHandle;

        /// <summary>
        /// VST3Plugin_ProcessAudio from the already loaded library, so Rust does not load it twice.
        /// Zero when the export is missing.
        /// </summary>
        internal IntPtr NativeProcessAudioPointer =>
            _threaded.LibraryHandle != IntPtr.Zero
            && NativeLibrary.TryGetExport(_threaded.LibraryHandle, "VST3Plugin_ProcessAudio", out IntPtr fn)
                ? fn
                : IntPtr.Zero;

        /// <summary>
        /// Bypass at host level, so JUCE runs processBlockBypassed and keeps the plugin latency.
        /// A dry/wet switch on our side would jump in time instead. This is how native mode
        /// honours Enabled without a click.
        /// </summary>
        internal void SetNativeBypass(bool bypassed) => _threaded.SetBypass(bypassed);

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
        /// Keeps the config and grabs the working buffers. No InitializeAsync here — the plugin
        /// has to be Ready already, so call VST3PluginHost.InitializeAudioAsync first.
        /// Buffers go by the wider of the mixer and the plugin channel count, otherwise a plugin
        /// reporting fewer channels than the config would leave Process() indexing off the end.
        /// </summary>
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

            var inner = _threaded.InnerWrapper;
            int _pluginChannels = inner?.ActualOutputChannels ?? 0;
            int _channels = Math.Max(config.Channels, _pluginChannels);

            _allocateBuffers(config.BufferSize, _channels);
            _buffersAllocated = true;
        }

        #endregion

        #region IEffectProcessor – Process (audio thread)

        /// <summary>
        /// Pushes the block through the plugin. Pass-through when we are off, not ready or dead.
        /// The wrapper drains the UI queue before the block, so params land here.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_disposed || !_enabled || !_buffersAllocated || !_threaded.IsReady)
                return;

            int channels     = _config!.Channels;
            int totalSamples = frameCount * channels;

            if (frameCount > _allocatedBlockSize)
                _allocateBuffers(frameCount, _allocatedChannels);

            bool needsDryMix = _mix < 0.999f;
            if (needsDryMix) buffer.Slice(0, totalSamples).CopyTo(_dryBuffer.AsSpan());

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
        /// Wipes our buffers and parks the transport. Does not re-init the plugin, that would block.
        /// Mixer/source Stop() calls this.
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
                    Array.Clear(_planarInputBuffers[ch]);
                    if (_planarOutputBuffers != null) Array.Clear(_planarOutputBuffers[ch]);
                }
            }

            if (_dryBuffer != null) Array.Clear(_dryBuffer);
        }

        #endregion

        #region VST-specific transport / parameter helpers

        /// <summary>
        /// Tempo in BPM. Lock-free enqueue, lands on the next block.
        /// </summary>
        public void SetTempo(double bpm) => _threaded.SetTempo(bpm);

        /// <summary>
        /// Transport play/stop flag.
        /// </summary>
        public void SetTransportPlaying(bool playing) => _threaded.SetTransportState(playing);

        /// <summary>
        /// Rewinds the transport sample position.
        /// </summary>
        public void ResetPosition() => _threaded.ResetTransportPosition();

        /// <summary>
        /// Param change via the SPSC queue, applied before the next block.
        /// </summary>
        public void SetParameter(int id, double value) => _threaded.SetParameter(id, value);

        /// <summary>
        /// Bulk param set on the plugin thread instead of the audio queue, so the native
        /// controller updates right away without waiting for a drain cycle.
        /// For cold stuff like project load, not for realtime.
        /// </summary>
        public async Task ApplyParametersAsync(IReadOnlyDictionary<int, double> parameters)
        {
            foreach (var kv in parameters)
                await _threaded.SetParameterAsync(kv.Key, kv.Value).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads every param through InnerWrapper. Only valid once the plugin is Ready.
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
        /// Editor size the plugin would like, null when it has no opinion.
        /// </summary>
        public (int Width, int Height)? GetEditorSize()
        {
            var size = _threaded?.InnerWrapper?.GetEditorSize();
            return size is null ? null : (size.Value.Width, size.Value.Height);
        }

        #endregion

        /// <summary>
        /// Drops our buffers and stops the transport. The ThreadedVst3Wrapper stays alive —
        /// it belongs to the host, dispose that once the engine is stopped.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            _threaded.SetTransportState(false);
            _threaded.ResetTransportPosition();

            _planarInputBuffers  = null;
            _planarOutputBuffers = null;
            _dryBuffer           = null;
            _buffersAllocated    = false;
        }

        #region Private helpers

        private void _allocateBuffers(int blockSize, int channels)
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
            _allocatedChannels = channels;
        }

        #endregion

        /// <summary>
        /// Diagnostics only.
        /// </summary>
        public override string ToString() =>
            $"VST3: {_name} ({Vendor}), Ready={IsReady}, Enabled={_enabled}, Mix={_mix:F2}";
    }
}
