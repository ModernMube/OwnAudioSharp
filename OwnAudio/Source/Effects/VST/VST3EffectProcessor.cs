using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Logger;
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

        private readonly NativeEffectEngine _native = new NativeEffectEngine();

        private bool _buffersAllocated;

        /// <summary>
        /// Channel count the engine bridge was built for: the wider of the mixer's and the
        /// plugin's, so a plugin asking for more than the mixer runs still gets its planes.
        /// </summary>
        private int _bridgeChannels;

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
        /// Can the engine call the plugin directly? Needs a live handle plus a resolvable
        /// process entry point. False means there is no audio path at all — both the mixer
        /// twin and Process() go through the rust bridge now.
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
            {
                Log.Error($"[VST3] '{_name}' cannot be initialized, the plugin is in state {_threaded.State}");
                throw new InvalidOperationException(
                    $"VST3 plugin '{_name}' is not audio-initialized. " +
                    $"Call and await VST3PluginHost.InitializeAudioAsync(sampleRate, blockSize) " +
                    $"before adding this processor to an effect chain. " +
                    $"Current state: {_threaded.State}");
            }

            _config = config ?? throw new ArgumentNullException(nameof(config));

            var inner = _threaded.InnerWrapper;
            int _pluginChannels = inner?.ActualOutputChannels ?? 0;
            int _channels = Math.Max(config.Channels, _pluginChannels);

            _bridgeChannels = _channels;
            _buffersAllocated = true;

            _native.InitializeVst(this, config.SampleRate, _bridgeChannels, config.BufferSize);

            if (_pluginChannels > config.Channels)
                Log.Warning($"[VST3] '{_name}' wants {_pluginChannels} channels but the mixer runs {config.Channels}, buffers sized to {_channels}");

            Log.Info($"[VST3] Processor '{_name}' initialized: {_channels}ch, block {config.BufferSize}, {LatencySamples} samples latency");
        }

        #endregion

        #region IEffectProcessor – Process (audio thread)

        /// <summary>
        /// Hands the block to the same engine bridge the mixer twin runs on, so a direct call
        /// and a mixer chain sound alike. Bypass and dry/wet happen in the bridge, delayed by
        /// the plugin latency; the plugin itself sees every block either way.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_disposed || !_buffersAllocated || !_threaded.IsReady)
                return;

            _native.EnsureVstBlock(this, _config!.SampleRate, _bridgeChannels, frameCount);
            _native.Process(this, buffer, frameCount);
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

            _native.Reset();
            _threaded.SetTransportState(false);
            _threaded.ResetTransportPosition();
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

            _native.Dispose();
            _threaded.SetTransportState(false);
            _threaded.ResetTransportPosition();

            _buffersAllocated    = false;

            Log.Info($"[VST3] Processor '{_name}' disposed");
        }

        #region Private helpers

        #endregion

        /// <summary>
        /// Diagnostics only.
        /// </summary>
        public override string ToString() =>
            $"VST3: {_name} ({Vendor}), Ready={IsReady}, Enabled={_enabled}, Mix={_mix:F2}";
    }
}
