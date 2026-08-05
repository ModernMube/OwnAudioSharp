using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// The transcriber seam that lets MT3 replace BasicPitch without ChordDetect knowing.
/// No model files and no audio hardware needed — that's the whole point of the interface.
/// </summary>
public class NoteTranscriberTests
{
    /// <summary>
    /// Hands back a canned note list and records what it was asked for.
    /// </summary>
    private sealed class FakeTranscriber : INoteTranscriber
    {
        private readonly List<Note> _notes;

        public FakeTranscriber(int sampleRate, params Note[] notes)
        {
            PreferredSampleRate = sampleRate;
            _notes = new List<Note>(notes);
        }

        public int PreferredSampleRate { get; }

        public int SeenSampleRate { get; private set; }

        public double LastProgress { get; private set; }

        public IReadOnlyList<Note> Transcribe(float[] samples, int sampleRate, Action<double>? progress = null)
        {
            SeenSampleRate = sampleRate;
            progress?.Invoke(1.0);
            LastProgress = 1.0;
            return _notes;
        }
    }

    [Fact]
    public void Transcriber_gets_the_rate_it_asked_for()
    {
        var fake = new FakeTranscriber(16000);

        fake.Transcribe(new float[16000], fake.PreferredSampleRate);

        fake.SeenSampleRate.Should().Be(16000);
    }

    [Fact]
    public void Progress_callback_is_forwarded()
    {
        var fake = new FakeTranscriber(22050);
        double seen = 0;

        fake.Transcribe(new float[100], 22050, p => seen = p);

        seen.Should().Be(1.0);
    }

    [Fact]
    public void BasicPitch_transcriber_still_runs_at_22050()
    {
        using var transcriber = new BasicPitchTranscriber();

        transcriber.PreferredSampleRate.Should().Be(Constants.AUDIO_SAMPLE_RATE);
    }

    [Fact]
    public void Notes_carry_program_and_drum_labels()
    {
        var drum = new Note(0f, 0.01f, 36, 0.8f, null, 0, true);
        var bass = new Note(0f, 1f, 40, 0.9f, null, 33, false);

        drum.IsDrum.Should().BeTrue();
        bass.IsDrum.Should().BeFalse();
        bass.Program.Should().Be(33);
    }

    [Fact]
    public void Notes_from_the_old_constructor_are_pitched_and_unlabelled()
    {
        var note = new Note(0f, 1f, 60, 0.5f, null);

        note.Program.Should().Be(0);
        note.IsDrum.Should().BeFalse();
    }
}
