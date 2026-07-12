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

public static class ChordDetect
{
    public static (List<TimedChord>, MusicalKey, int) DetectFromFile( string audioFile, float intervalSecond = 1.0f)
    {
        if (!File.Exists(audioFile))
            throw new AudioException("Source is not loaded.");

#nullable disable
        IAudioDecoder _decoder = AudioDecoderFactory.Create(audioFile, 22050, 1);
        var samples = _decoder.ReadAllSamples();
        var _waveBuffer = new WaveBuffer(samples);
#nullable restore

        using var model = new Model();
        var modelOutput = model.Predict(_waveBuffer, progress =>
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

        var converter = new NotesConverter(modelOutput);
        List<OwnaudioNET.Features.Extensions.Note> rawNotes = converter.Convert(convertOptions);

        int detectTempo = DetectBpmFromSamples(samples, 22050, 1);

        var analyzer = new SongChordAnalyzer(
                windowSize: intervalSecond,        // fallback if bpm = 0
                hopSize: 0.5f,
                minimumChordDuration: 0.5f,
                confidence: 0.65f,
                bpm: detectTempo           // derive quarter-note window from detected BPM
            );

        var chords = analyzer.AnalyzeSong(rawNotes);
        MusicalKey? detectedKey = analyzer.DetectedKey;

        _decoder.Dispose();
#nullable disable
        return (chords, detectedKey, detectTempo);
#nullable restore
    }

    /// <summary>
    /// Detects chords from multiple audio files by mixing them into a single audio stream.
    /// Useful for multi-track projects where each track is a separate file.
    /// </summary>
    /// <param name="audioFiles">List of audio file paths to mix and analyze.</param>
    /// <param name="intervalSecond">Analysis window size in seconds.</param>
    /// <returns>Tuple of timed chords, detected musical key, and tempo BPM.</returns>
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

            using var decoder = AudioDecoderFactory.Create(file, targetSampleRate, targetChannels);
            float[] samples = decoder.ReadAllSamples();
            if (samples.Length > maxLength)
                maxLength = samples.Length;
            allTrackSamples.Add(samples);
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

        var waveBuffer = new WaveBuffer(mixed);

        using var model = new Model();
        var modelOutput = model.Predict(waveBuffer, progress =>
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

        var converter = new NotesConverter(modelOutput);
        List<Note> rawNotes = converter.Convert(convertOptions);

        int detectTempo = DetectBpmFromSamples(mixed, 22050, 1);

        var analyzer = new SongChordAnalyzer(
            windowSize: intervalSecond,        // fallback if bpm = 0
            hopSize: 0.5f,
            minimumChordDuration: 0.5f,
            confidence: 0.65f,
            bpm: detectTempo           // derive quarter-note window from detected BPM
        );

        var chords = analyzer.AnalyzeSong(rawNotes);
        MusicalKey? detectedKey = analyzer.DetectedKey;

#nullable disable
        return (chords, detectedKey, detectTempo);
#nullable restore
    }

    /// <summary>
    /// Detects BPM from raw audio samples using SoundTouch auto-correlation algorithm.
    /// Falls back to 120 BPM if detection fails (e.g. speech or non-rhythmic content).
    /// </summary>
    private static int DetectBpmFromSamples(float[] samples, int sampleRate, int channels)
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

    /// <summary>
    /// Guards the cached real-time detector against concurrent callers.
    /// </summary>
    private static readonly object RealtimeDetectorLock = new object();

    /// <summary>
    /// The cached real-time detector reused across calls so its internal stability
    /// buffer can accumulate history; a fresh instance per call would reset the
    /// buffer and make the reported stability meaningless.
    /// </summary>
    private static RealTimeChordDetector? _realtimeDetector;

    /// <summary>
    /// The detection mode the cached real-time detector was created with.
    /// </summary>
    private static DetectionMode _realtimeDetectorMode;

    /// <summary>
    /// The buffer size the cached real-time detector was created with.
    /// </summary>
    private static int _realtimeDetectorBufferSize;

    /// <summary>
    /// Processes a group of notes with the shared real-time detector and returns the most
    /// stable chord. The detector instance persists across calls (and is recreated only
    /// when the mode or buffer size changes), so the stability score reflects the last
    /// <paramref name="buffersize"/> calls rather than a single frame.
    /// </summary>
    /// <param name="notes">The notes of the current analysis frame.</param>
    /// <param name="mode">The detection mode to use.</param>
    /// <param name="buffersize">The number of recent frames considered for stability.</param>
    /// <returns>A tuple containing the most stable chord name and its stability score (0.0 to 1.0).</returns>
    public static (string chord, float stability) DetectRealtime(
        List<Note> notes,
        DetectionMode mode = DetectionMode.Optimized,
        int buffersize = 5)
    {
        lock (RealtimeDetectorLock)
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
