using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Shared scaffolding for the built-in effects. All of them are param models over a
    /// native twin now — the DSP runs in Rust — so lifecycle, id and the engine handoff are
    /// the same everywhere and only the parameters differ.
    /// </summary>
    public abstract class NativeBackedEffect
    {
        /// <summary>
        /// Our own native effect instance. Same DSP as the mixer twin, separate handle.
        /// private protected because the engine itself is internal — subclasses live here anyway.
        /// </summary>
        private protected readonly NativeEffectEngine _native = new NativeEffectEngine();

        /// <summary>
        /// Engine config we were initialized with, null until Initialize runs.
        /// </summary>
        private protected AudioConfig? _config;

        /// <summary>
        /// Display name. Subclasses decide whether it is settable, so the property stays theirs.
        /// </summary>
        private protected string _name;

        private readonly IEffectProcessor _self;
        private bool _disposed;

        /// <summary>
        /// Every subclass has to be an IEffectProcessor — the native engine looks the adapter
        /// up off the instance — so we check it once here instead of casting on every call.
        /// </summary>
        /// <param name="name">display name, what Name hands back</param>
        private protected NativeBackedEffect(string name)
        {
            _self = this as IEffectProcessor
                ?? throw new InvalidOperationException($"{GetType().Name} has to implement IEffectProcessor");
            _name = name;
        }

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Takes the engine config and builds the native twin. Subclasses hook OnInitialize
        /// if they keep anything that depends on the rate.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            OnInitialize(config);
            _native.Initialize(_self, config);
        }

        /// <summary>
        /// Same DSP the mixer twin runs, on this instance's native handle.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            _native.Process(_self, buffer, frameCount);
        }

        /// <summary>
        /// Drops the native tail and whatever managed state the subclass keeps.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
            ResetState();
        }

        /// <summary>
        /// Releases the native handle. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _native.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Called before the native twin is built, for anything that follows the sample rate.
        /// </summary>
        /// <param name="config"></param>
        private protected virtual void OnInitialize(AudioConfig config) { }

        /// <summary>
        /// Clears subclass state on Reset. Most effects keep none, their tail is native.
        /// </summary>
        private protected virtual void ResetState() { }
    }
}
