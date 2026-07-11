namespace Ownaudio.Audio.Effects;

/// <summary>
/// Identifies the variant of a native Rust-backed audio effect.
/// </summary>
/// <remarks>
/// <para>
/// Numeric values are part of the stable C ABI and must stay in sync with
/// <c>ownaudio-core/src/effects/mod.rs</c> and
/// <c>Ownaudio.Native.RustAudio.Enums.NativeEffectType</c>.
/// </para>
/// </remarks>
public enum EffectType : uint
{
    /// <summary>Freeverb algorithmic reverb.</summary>
    Reverb = 0,

    /// <summary>10-band parametric equalizer.</summary>
    Equalizer = 1,

    /// <summary>Dynamic range compressor with soft knee.</summary>
    Compressor = 2,

    /// <summary>Look-ahead brick-wall limiter.</summary>
    Limiter = 3,

    /// <summary>Stereo delay with ping-pong and damping.</summary>
    Delay = 4,

    /// <summary>Multi-voice chorus with LFO modulation.</summary>
    Chorus = 5,

    /// <summary>Soft-clipping distortion.</summary>
    Distortion = 6,

    /// <summary>Asymmetric tube overdrive.</summary>
    Overdrive = 7,

    /// <summary>Flanger with short modulated delay.</summary>
    Flanger = 8,

    /// <summary>Phaser with all-pass filter stages.</summary>
    Phaser = 9,

    /// <summary>Rotary / Leslie-cabinet speaker simulator.</summary>
    Rotary = 10,

    /// <summary>Automatic gain control.</summary>
    AutoGain = 11,

    /// <summary>Harmonic enhancer / exciter.</summary>
    Enhancer = 12,

    /// <summary>Noise gate / dynamic amplifier.</summary>
    Gate = 13,

    /// <summary>Real-time pitch shifter.</summary>
    PitchShift = 14,

    /// <summary>Adaptive dynamic amplifier — dual-window RMS AGC with noise gate.</summary>
    DynamicAmp = 15,

    /// <summary>30-band 1/3-octave parametric equalizer.</summary>
    Equalizer30 = 16,

    /// <summary>External VST3 plugin hosted through a C ABI process callback.</summary>
    Vst = 17,

    /// <summary>
    /// SmartMaster composite mastering chain (graphic EQ → subharmonic →
    /// compressor → crossover/phase alignment → limiter), hosted as one native effect.
    /// </summary>
    SmartMaster = 18,
}
