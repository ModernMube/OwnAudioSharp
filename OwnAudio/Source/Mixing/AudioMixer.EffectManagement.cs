using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Appends a master effect to the chain. VST3 needs InitializeAudioAsync done first,
    /// otherwise IsReady is false and we bail.
    /// </summary>
    /// <param name="effect"></param>
    public void AddMasterEffect(IEffectProcessor effect)
    {
        _throwIfDisposed();

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

        //The managed DSP is inert without a MixThread, so route it onto the native master bus
        AttachMasterEffectToRust(effect);
    }

    /// <summary>
    /// Drops a master effect and its native twin off the bus.
    /// </summary>
    /// <param name="effect"></param>
    /// <returns></returns>
    public bool RemoveMasterEffect(IEffectProcessor effect)
    {
        _throwIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        bool _removed;
        lock (_effectsLock) { _removed = _masterEffects.Remove(effect); }

        if (_removed) DetachMasterEffectFromRust(effect);

        return _removed;
    }

    /// <summary>
    /// Wipes the whole master chain, native side included.
    /// </summary>
    public void ClearMasterEffects()
    {
        _throwIfDisposed();

        lock (_effectsLock) { _masterEffects.Clear(); }

        ClearRustMasterEffects();
    }

    /// <summary>
    /// Snapshot of the registered master effects. Allocates, so keep it off the audio thread.
    /// </summary>
    /// <returns></returns>
    public IEffectProcessor[] GetMasterEffects()
    {
        lock (_effectsLock) return _masterEffects.ToArray();
    }
}
