using Logger;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using OwnaudioNET.Exceptions;
using Ownaudio.Decoders;
using Ownaudio.Core;
using Ownaudio.Safe;

namespace OwnaudioNET.Features.OwnChordDetect;

/// <summary>
/// One-shot chord detection: file (or files) in, chord list + key + tempo out.
/// </summary>
public static class ChordDetect
{
    /// <summary>
    /// Runs the whole chain on one file — decode, note transcription, chord analysis.
    /// intervalSecond is only the fallback window when we can't find a tempo.
    /// </summary>
    public static (List<TimedChord>, MusicalKey, int) DetectFromFile(string audioFile, float intervalSecond = 1.0f)
    {
        if (!File.Exists(audioFile))
            throw new AudioException("Source is not loaded.");

#nullable disable
        IAudioDecoder _decoder = AudioDecoderFactory.Create(audioFile, 22050, 1);
        var samples = _decoder.ReadAllSamples();
        _decoder.Dispose();

        return _analyze(samples, intervalSecond);
#nullable restore
    }

    /// <summary>
    /// Same thing for a multitrack project — everything gets summed to one mono stream first.
    /// </summary>
    public static (List<TimedChord>, MusicalKey, int) DetectFromFiles(
        IReadOnlyList<string> audioFiles,
        float intervalSecond = 1.0f)
    {
        if (audioFiles == null || audioFiles.Count == 0)
            throw new AudioException("No audio files provided.");

        var allTrackSamples = new List<float[]>(audioFiles.Count);
        int maxLength = 0;

        foreach (var file in audioFiles)
        {
            if (!File.Exists(file))
                throw new AudioException($"Audio file not found: {file}");

            using (var decoder = AudioDecoderFactory.Create(file, 22050, 1))
            {
                float[] _samples = decoder.ReadAllSamples();
                if (_samples.Length > maxLength) maxLength = _samples.Length;
                allTrackSamples.Add(_samples);
            }
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

#nullable disable
        return _analyze(mixed, intervalSecond);
#nullable restore
    }

#nullable disable
    private static (List<TimedChord>, MusicalKey, int) _analyze(float[] samples, float intervalSecond)
    {
        using var model = new Model();
        var modelOutput = model.Predict(new WaveBuffer(samples), progress =>
        {
            Log.Info($"\rRecognizing musical notes: {progress:P1}");
        });

        var convertOptions = new NotesConvertOptions
        {
            OnsetThreshold = 0.5f,
            FrameThreshold = 0.2f,
            MinNoteLength = 15,
            MinFreq = 32.7f,
            MaxFreq = 2800f,
            IncludePitchBends = false,
            MelodiaTrick = true
        };

        var rawNotes = new NotesConverter(modelOutput).Convert(convertOptions);
        int detectTempo = _detectBpm(samples, 22050, 1);

        var analyzer = new SongChordAnalyzer(
            windowSize: intervalSecond,
            hopSize: 0.5f,
            minimumChordDuration: 0.5f,
            confidence: 0.65f,
            bpm: detectTempo);

        return (analyzer.AnalyzeSong(rawNotes), analyzer.DetectedKey, detectTempo);
    }
#nullable restore

    private static int _detectBpm(float[] samples, int sampleRate, int channels)
    {
        const int chunkSize = 4096;
        using var bpmDetect = new BpmDetect(channels, sampleRate);

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

    private static readonly object _realtimeLock = new object();

    private static RealTimeChordDetector? _realtimeDetector;
    private static DetectionMode _realtimeDetectorMode;
    private static int _realtimeDetectorBufferSize;

    /// <summary>
    /// Realtime path. The detector is kept between calls on purpose — a fresh one every frame
    /// would wipe the history and the stability number would mean nothing. Rebuilt only when
    /// mode or buffersize changes.
    /// </summary>
    /// <returns>The steadiest chord over the last buffersize calls plus its stability, 0..1.</returns>
    public static (string chord, float stability) DetectRealtime(
        List<Note> notes,
        DetectionMode mode = DetectionMode.Optimized,
        int buffersize = 5)
    {
        lock (_realtimeLock)
        {
            if (_realtimeDetector == null
                || _realtimeDetectorMode != mode
                || _realtimeDetectorBufferSize != buffersize)
            {
                _realtimeDetector = new RealTimeChordDetector(buffersize, mode);
                _realtimeDetectorMode = mode;
                _realtimeDetectorBufferSize = buffersize;
            }

            return _realtimeDetector.ProcessNotes(notes);
        }
    }
}
