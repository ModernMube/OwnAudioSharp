using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// Tuning values the whole BasicPitch pipeline runs on.
/// </summary>
public static class Constants
{
    /// <summary>
    /// FFT hop in samples.
    /// </summary>
    public const int FFT_HOP = 256;

    /// <summary>
    /// How many frames two neighbouring windows share.
    /// </summary>
    public const int N_OVERLAPPING_FRAMES = 30;

    /// <summary>
    /// Window overlap in samples.
    /// </summary>
    public const int OVERLAP_LEN = N_OVERLAPPING_FRAMES * FFT_HOP;

    /// <summary>
    /// Rate the model expects.
    /// </summary>
    public const int AUDIO_SAMPLE_RATE = 22050;

    /// <summary>
    /// Window length, seconds.
    /// </summary>
    public const int AUDIO_WINDOW_LEN = 2;

    /// <summary>
    /// Samples in one window.
    /// </summary>
    public const int AUDIO_N_SAMPLES = AUDIO_SAMPLE_RATE * AUDIO_WINDOW_LEN - FFT_HOP;

    /// <summary>
    /// Distance between two window starts.
    /// </summary>
    public const int HOP_SIZE = AUDIO_N_SAMPLES - OVERLAP_LEN;

    /// <summary>
    /// Annotation frames per second.
    /// </summary>
    public const int ANNOTATIONS_FPS = AUDIO_SAMPLE_RATE / FFT_HOP;

    /// <summary>
    /// Bin 0 sits on MIDI 21 (A0).
    /// </summary>
    public const int MIDI_OFFSET = 21;

    /// <summary>
    /// Top usable freq bin.
    /// </summary>
    public const int MAX_FREQ_IDX = 87;

    /// <summary>
    /// Annotation frames in one window.
    /// </summary>
    public const int ANNOT_N_FRAMES = ANNOTATIONS_FPS * AUDIO_WINDOW_LEN;

    /// <summary>
    /// Contour resolution: bins per semitone.
    /// </summary>
    public const int CONTOURS_BINS_PER_SEMITONE = 3;

    /// <summary>
    /// Semitones covered by the annotation range.
    /// </summary>
    public const int ANNOTATIONS_N_SEMITONES = 88;

    /// <summary>
    /// A0 in Hz.
    /// </summary>
    public const float ANNOTATIONS_BASE_FREQUENCY = 27.5f;

    /// <summary>
    /// All contour bins together.
    /// </summary>
    public const int N_FREQ_BINS_CONTOURS = ANNOTATIONS_N_SEMITONES * CONTOURS_BINS_PER_SEMITONE;

    /// <summary>
    /// Pitch bend range in MIDI ticks.
    /// </summary>
    public const int N_PITCH_BEND_TICKS = 8192;
}
