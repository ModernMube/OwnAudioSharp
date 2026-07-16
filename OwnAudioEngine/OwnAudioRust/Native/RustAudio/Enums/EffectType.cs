namespace Ownaudio.Native.RustAudio.Enums;

/// <summary>
/// Effect ids for the rust side. Keep in sync with effects/mod.rs and Audio.Effects.EffectType.
/// </summary>
internal enum NativeEffectType : uint
{
    Reverb = 0,             //Freeverb
    Equalizer = 1,          //10 band
    Compressor = 2,         //soft knee
    Limiter = 3,            //look-ahead brickwall
    Delay = 4,              //ping-pong + damping
    Chorus = 5,
    Distortion = 6,
    Overdrive = 7,          //asymmetric tube
    Flanger = 8,
    Phaser = 9,
    Rotary = 10,            //Leslie sim
    AutoGain = 11,
    Enhancer = 12,          //exciter
    Gate = 13,
    PitchShift = 14,
    DynamicAmp = 15,        //dual-window RMS agc + gate
    Equalizer30 = 16,       //1/3 octave
    Vst = 17,               //external plugin over C ABI
    SmartMaster = 18,       //whole mastering chain as one effect
}
