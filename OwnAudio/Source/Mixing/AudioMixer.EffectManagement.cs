using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Adds a master effect to the end of the processing chain.
    /// Effects are processed in insertion order during each mix cycle.
    /// The effect must report <see cref="IEffectProcessor.IsReady"/> before it can be added;
    /// for VST3 plug-ins this means <c>VST3PluginHost.InitializeAudioAsync()</c> must have completed.
    /// </summary>
    /// <param name="effect">The effect processor to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="effect"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the effect is not yet ready for audio processing.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void AddMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        if (!effect.IsReady)
            throw new InvalidOperationException(
                $"Effect '{effect.Name}' is not ready for audio processing. " +
                $"For VST3 effects call and await VST3PluginHost.InitializeAudioAsync() first.");

        lock (_effectsLock)
        {
            effect.Initialize(_config);
            _masterEffects.Add(effect);
        }

        // In the Rust-native chain the managed effect's DSP is inert (no MixThread); route it onto
        // the native master bus so it actually processes the mix.
        AttachMasterEffectToRust(effect);
    }

    /// <summary>
    /// Removes the specified master effect from the processing chain.
    /// The paired native master effect is detached from the Rust master bus after removal,
    /// so the audio thread stops processing the effect promptly.
    /// </summary>
    /// <param name="effect">The effect processor to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the effect was found and removed;
    /// <see langword="false"/> if it was not in the chain.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="effect"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public bool RemoveMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        bool removed;
        lock (_effectsLock)
        {
            removed = _masterEffects.Remove(effect);
        }

        if (removed)
        {
            DetachMasterEffectFromRust(effect);
        }

        return removed;
    }

    /// <summary>
    /// Removes all effects from the master processing chain.
    /// Every paired native master effect is removed from the Rust master bus after clearing,
    /// so the audio thread stops processing them promptly.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void ClearMasterEffects()
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            _masterEffects.Clear();
        }

        ClearRustMasterEffects();
    }

    /// <summary>
    /// Returns a point-in-time snapshot array of all currently registered master effects.
    /// Each call allocates a new array under the effects lock; do not call from the real-time audio thread.
    /// </summary>
    /// <returns>A new array containing the registered <see cref="IEffectProcessor"/> instances in order.</returns>
    public IEffectProcessor[] GetMasterEffects()
    {
        lock (_effectsLock)
        {
            return _masterEffects.ToArray();
        }
    }

}
