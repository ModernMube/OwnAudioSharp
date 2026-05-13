using Logger;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using OwnaudioNET.Exceptions;
using Ownaudio.Decoders;
using Ownaudio.Core;
using SoundTouch;

namespace OwnaudioNET.Features.OwnChordDetect;

public static class ChordDetect
{
    public static (List<TimedChord>, MusicalKey, int) DetectFromFile(string audioFile, float intervalSecond = 1.0f)
    {
        if (!File.Exists(audioFile))
            throw new AudioException("Source is not loaded.");

        using var decoder = AudioDecoderFactory.Create(audioFile, 22050, 1);
        var samples = decoder.ReadAllSamples();

        int detectTempo = DetectBpmFromSamples(samples, 22050, 1);

        var detectedChords = OwnAudio.ML.ChordDetector.DetectAsync(samples, 22050)
            .GetAwaiter().GetResult();

        var timedChords = detectedChords
            .Select(c => new TimedChord(c.StartTime, c.EndTime, c.Name, c.Confidence, Array.Empty<string>()))
            .ToList();

#nullable disable
        return (timedChords, null, detectTempo);
#nullable restore
    }

    public static (List<TimedChord>, MusicalKey, int) DetectFromFiles(
        IReadOnlyList<string> audioFiles,
        float intervalSecond = 1.0f)
    {
        if (audioFiles == null || audioFiles.Count == 0)
            throw new AudioException("No audio files provided.");

        const int targetSampleRate = 22050;
        const int targetChannels = 1;

        var allTrackSamples = new List<float[]>(audioFiles.Count);
        int maxLength = 0;

        foreach (var file in audioFiles)
        {
            if (!File.Exists(file))
                throw new AudioException($"Audio file not found: {file}");

            using var dec = AudioDecoderFactory.Create(file, targetSampleRate, targetChannels);
            float[] s = dec.ReadAllSamples();
            if (s.Length > maxLength) maxLength = s.Length;
            allTrackSamples.Add(s);
        }

        var mixed = new float[maxLength];
        foreach (var trackSamples in allTrackSamples)
        {
            for (int i = 0; i < trackSamples.Length; i++)
                mixed[i] += trackSamples[i];
        }

        float peak = 0f;
        for (int i = 0; i < mixed.Length; i++)
        {
            float abs = Math.Abs(mixed[i]);
            if (abs > peak) peak = abs;
        }
        if (peak > 1f)
        {
            float inv = 1f / peak;
            for (int i = 0; i < mixed.Length; i++)
                mixed[i] *= inv;
        }

        int detectTempo = DetectBpmFromSamples(mixed, 22050, 1);

        var detectedChords = OwnAudio.ML.ChordDetector.DetectAsync(mixed, 22050)
            .GetAwaiter().GetResult();

        var timedChords = detectedChords
            .Select(c => new TimedChord(c.StartTime, c.EndTime, c.Name, c.Confidence, Array.Empty<string>()))
            .ToList();

#nullable disable
        return (timedChords, null, detectTempo);
#nullable restore
    }

    private static int DetectBpmFromSamples(float[] samples, int sampleRate, int channels)
    {
        const int chunkSize = 4096;
        var bpmDetect = new BpmDetect(channels, sampleRate);

        int offset = 0;
        while (offset < samples.Length)
        {
            int count = Math.Min(chunkSize, samples.Length - offset);
            bpmDetect.InputSamples(samples.AsSpan(offset, count), count / channels);
            offset += count;
        }

        float bpm = bpmDetect.GetBpm();
        return bpm > 0 ? (int)Math.Round(bpm) : 120;
    }

    public static (string chord, float stability) DetectRealtime(
        List<Note> notes,
        DetectionMode mode = DetectionMode.Optimized,
        int buffersize = 5)
    {
        var realtimeDetector = new RealTimeChordDetector(buffersize, mode);
        return realtimeDetector.ProcessNotes(notes);
    }
}
