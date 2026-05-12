using System.Runtime.InteropServices;

namespace OwnAudio.ML.Interop;

/// <summary>Native result struct for vocal separation.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSeparationResult
{
    public float* Vocals;
    public float* Instrumental;
    public int SampleCount;
}

/// <summary>Native result struct for a single detected chord.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativeChordResult
{
    public float StartTime;
    public float EndTime;
    public fixed byte ChordName[32];
    public float Confidence;
}

/// <summary>Native struct for audio spectrum analysis (30-band).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAudioSpectrum
{
    public fixed float FrequencyBands[30];
    public float RmsLevel;
    public float PeakLevel;
    public float Loudness;
    public float DynamicRange;
}
