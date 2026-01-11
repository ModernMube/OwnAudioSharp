using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Adds a master effect to the processing chain.
    /// Effects are processed in the order they are added.
    /// </summary>
    /// <param name="effect">The effect to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void AddMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        lock (_effectsLock)
        {
            // Initialize effect with current config
            effect.Initialize(_config);
            _masterEffects.Add(effect);

            // Mark effects as changed to trigger cache update
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Removes a master effect from the processing chain.
    /// </summary>
    /// <param name="effect">The effect to remove.</param>
    /// <returns>True if removed successfully, false if not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        lock (_effectsLock)
        {
            bool removed = _masterEffects.Remove(effect);
            if (removed)
            {
                // Mark effects as changed to trigger cache update
                _effectsChanged = true;
            }
            return removed;
        }
    }

    /// <summary>
    /// Clears all master effects from the processing chain.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void ClearMasterEffects()
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            _masterEffects.Clear();

            // Mark effects as changed to trigger cache update
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Gets all master effects.
    /// </summary>
    /// <returns>Array of master effects (snapshot).</returns>
    public IEffectProcessor[] GetMasterEffects()
    {
        lock (_effectsLock)
        {
            return _masterEffects.ToArray();
        }
    }
}
