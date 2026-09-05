using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// One detected note: when, which pitch, how loud, plus optional bend curve.
/// </summary>
public sealed class Note : IComparable<Note>
{
    /// <summary>
    /// Start, seconds.
    /// </summary>
    public readonly float StartTime;

    /// <summary>
    /// End, seconds.
    /// </summary>
    public readonly float EndTime;

    /// <summary>
    /// MIDI note number.
    /// </summary>
    public readonly int Pitch;

    /// <summary>
    /// 0..1 loudness, later scaled to velocity.
    /// </summary>
    public readonly float Amplitude;

    /// <summary>
    /// Bend curve, one value per model frame. Null if we didn't ask for bends.
    /// </summary>
    public float[]? PitchBend;

    /// <summary>
    /// MIDI program the note was played on. Always 0 from BasicPitch, which can't tell
    /// instruments apart — only MT3 fills this in.
    /// </summary>
    public readonly int Program;

    /// <summary>
    /// Percussion hit rather than a pitched note. Chord analysis wants these gone.
    /// </summary>
    public readonly bool IsDrum;

    /// <summary>
    /// Fills everything in one go.
    /// </summary>
    public Note(float startTime, float endTime, int pitch, float amplitude, float[]? pitchBend)
        : this(startTime, endTime, pitch, amplitude, pitchBend, 0, false) { }

    /// <summary>
    /// Same plus the instrument labels a multi-track transcriber gives us.
    /// </summary>
    public Note(float startTime, float endTime, int pitch, float amplitude, float[]? pitchBend, int program, bool isDrum)
    {
        StartTime = startTime;
        EndTime = endTime;
        Pitch = pitch;
        Amplitude = amplitude;
        PitchBend = pitchBend;
        Program = program;
        IsDrum = isDrum;
    }

    /// <summary>
    /// Debug dump.
    /// </summary>
    public override string ToString()
    {
        var nbend = PitchBend != null ? PitchBend!.Length : 0;
        return $"start: {StartTime}, end: {EndTime}, pitch: {Pitch}, amplitude: {Amplitude}, bend: ${nbend}[{string.Join(",", PitchBend ?? [])}]";
    }

    /// <summary>
    /// Sorts by start, then end, pitch, amplitude, bend length.
    /// </summary>
    public int CompareTo(Note? other)
    {
        if (other == null) return 1;

        float fcmp = StartTime - other.StartTime;
        if (fcmp != 0f) return Math.Sign(fcmp);

        fcmp = EndTime - other.EndTime;
        if (fcmp != 0f) return Math.Sign(fcmp);

        var icmp = Pitch - other.Pitch;
        if (icmp != 0) return Math.Sign(icmp);

        fcmp = Amplitude - other.Amplitude;
        if (fcmp != 0f) return Math.Sign(fcmp);

        var l = PitchBend == null ? -1 : PitchBend.Length;
        var r = other.PitchBend == null ? -1 : other.PitchBend.Length;

        return Math.Sign(l - r);
    }
}
