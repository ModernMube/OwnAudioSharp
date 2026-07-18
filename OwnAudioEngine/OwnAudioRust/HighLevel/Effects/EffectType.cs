namespace Ownaudio.Audio.Effects;

/// <summary>
/// Which native effect variant we are talking about. The numbers are part of
/// the C ABI, keep them in sync with ownaudio-core/src/effects/mod.rs and
/// with NativeEffectType.
/// </summary>
public enum EffectType : uint
{
    /// <summary>Freeverb algorithmic reverb.</summary>
    Reverb = 0,

    /// <summary>10 band parametric EQ.</summary>
    Equalizer = 1,

    /// <summary>Soft knee compressor.</summary>
    Compressor = 2,

    /// <summary>Look-ahead brickwall limiter.</summary>
    Limiter = 3,

    /// <summary>Stereo delay, ping-pong capable.</summary>
    Delay = 4,

    /// <summary>Multi voice chorus.</summary>
    Chorus = 5,

    /// <summary>Soft clipping distortion.</summary>
    Distortion = 6,

    /// <summary>Asymmetric tube style overdrive.</summary>
    Overdrive = 7,

    /// <summary>Flanger, short modulated delay.</summary>
    Flanger = 8,

    /// <summary>All-pass stage phaser.</summary>
    Phaser = 9,

    /// <summary>Leslie cabinet sim.</summary>
    Rotary = 10,

    /// <summary>Auto gain control.</summary>
    AutoGain = 11,

    /// <summary>Harmonic exciter.</summary>
    Enhancer = 12,

    /// <summary>Noise gate.</summary>
    Gate = 13,

    /// <summary>Realtime pitch shifter.</summary>
    PitchShift = 14,

    /// <summary>
    /// Adaptive dynamic amp, dual window RMS AGC with a gate.
    /// </summary>
    DynamicAmp = 15,

    /// <summary>30 band 1/3 octave EQ.</summary>
    Equalizer30 = 16,

    /// <summary>External VST3 plugin behind a C ABI process callback.</summary>
    Vst = 17,

    /// <summary>
    /// SmartMaster chain (graphic EQ, subharmonic, comp, crossover/phase, limiter)
    /// packed into one native effect.
    /// </summary>
    SmartMaster = 18,
}
