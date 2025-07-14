using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Microsoft.ML.OnnxRuntime;
using Ownaudio.Engines;
using Ownaudio.Sources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Reflection;

namespace Ownaudio.Utilities.Extensions;

#region Audio Processing

/// <summary>
/// Provides audio file reading and resampling functionality for BasicPitch audio processing.
/// </summary>
public class AudioReader : IDisposable
{
    private readonly SourceManager? _manager;
    private readonly string? _audioFilePath;

    /// <summary>
    /// Total duration of the audio file
    /// </summary>
    public TimeSpan? AudioDuration;

    /// <summary>
    /// Initializes a new instance of the AudioReader class.
    /// </summary>
    /// <param name="pathAudio">Path to the audio file to read.</param>
    public AudioReader(string pathAudio)
    {
        if (OwnAudio.Initialize() && File.Exists(pathAudio))
        {
            AudioEngineOutputOptions _out = new AudioEngineOutputOptions
            (
                device: OwnAudio.DefaultOutputDevice,
                channels: OwnAudioEngine.EngineChannels.Mono,
                sampleRate: 22050,
                latency: OwnAudio.DefaultOutputDevice.DefaultLowOutputLatency
            );

            SourceManager.OutputEngineOptions = _out;
            SourceManager.EngineFramesPerBuffer = 1024;

            _manager = SourceManager.Instance;
            _audioFilePath = pathAudio;
        }
    }

    /// <summary>
    /// Reads all audio data into a WaveBuffer with target format.
    /// </summary>
    /// <returns>WaveBuffer containing the resampled audio data.</returns>
    public WaveBuffer ReadAll()
    {
        if (_manager is not null && _audioFilePath is not null)
        {
            if (_manager.AddOutputSource(_audioFilePath, "PitchSource").Result)
            {
                WaveBuffer _waveBuffer = new WaveBuffer(_manager["PitchSource"].GetFloatAudioData(new TimeSpan(0)));
                AudioDuration = _manager["PitchSource"].Duration;
                return _waveBuffer;
            }
        }

        return new WaveBuffer();
    }

    /// <summary>
    /// Releases all resources used by the AudioReader.
    /// </summary>
    public void Dispose()
    {
        _manager?.Dispose();
    }
}

#endregion

#region Constants

/// <summary>
/// Contains all constants used throughout the BasicPitch processing pipeline.
/// </summary>
public static class Constants
{
    /// <summary>
    /// FFT hop size in samples.
    /// </summary>
    public const int FFT_HOP = 256;

    /// <summary>
    /// Number of overlapping frames between audio windows.
    /// </summary>
    public const int N_OVERLAPPING_FRAMES = 30;

    /// <summary>
    /// Length of overlap between audio windows in samples.
    /// </summary>
    public const int OVERLAP_LEN = N_OVERLAPPING_FRAMES * FFT_HOP;

    /// <summary>
    /// Target audio sample rate in Hz.
    /// </summary>
    public const int AUDIO_SAMPLE_RATE = 22050;

    /// <summary>
    /// Audio window length in seconds.
    /// </summary>
    public const int AUDIO_WINDOW_LEN = 2;

    /// <summary>
    /// Number of audio samples per window.
    /// </summary>
    public const int AUDIO_N_SAMPLES = AUDIO_SAMPLE_RATE * AUDIO_WINDOW_LEN - FFT_HOP;

    /// <summary>
    /// Hop size between audio windows in samples.
    /// </summary>
    public const int HOP_SIZE = AUDIO_N_SAMPLES - OVERLAP_LEN;

    /// <summary>
    /// Annotation frames per second.
    /// </summary>
    public const int ANNOTATIONS_FPS = AUDIO_SAMPLE_RATE / FFT_HOP;

    /// <summary>
    /// MIDI note offset for the lowest note.
    /// </summary>
    public const int MIDI_OFFSET = 21;

    /// <summary>
    /// Maximum frequency index for MIDI notes.
    /// </summary>
    public const int MAX_FREQ_IDX = 87;

    /// <summary>
    /// Number of annotation frames per audio window.
    /// </summary>
    public const int ANNOT_N_FRAMES = ANNOTATIONS_FPS * AUDIO_WINDOW_LEN;

    /// <summary>
    /// Number of frequency bins per semitone for contour analysis.
    /// </summary>
    public const int CONTOURS_BINS_PER_SEMITONE = 3;

    /// <summary>
    /// Number of semitones in the annotation range.
    /// </summary>
    public const int ANNOTATIONS_N_SEMITONES = 88;

    /// <summary>
    /// Base frequency for annotations in Hz (A0).
    /// </summary>
    public const float ANNOTATIONS_BASE_FREQUENCY = 27.5f;

    /// <summary>
    /// Total number of frequency bins for contour analysis.
    /// </summary>
    public const int N_FREQ_BINS_CONTOURS = ANNOTATIONS_N_SEMITONES * CONTOURS_BINS_PER_SEMITONE;

    /// <summary>
    /// Number of pitch bend ticks for MIDI output.
    /// </summary>
    public const int N_PITCH_BEND_TICKS = 8192;
}

#endregion

#region Math Utilities

/// <summary>
/// Provides mathematical utility functions for array operations and tensor calculations.
/// </summary>
public class MathTool
{
    /// <summary>
    /// Creates an array of values starting from a given value with a specific step size.
    /// </summary>
    /// <param name="start">Starting value.</param>
    /// <param name="step">Step size between consecutive values.</param>
    /// <param name="count">Number of values to generate.</param>
    /// <returns>Array of float values.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when count is negative.</exception>
    public static float[] ARange(float start, float step, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0) return Array.Empty<float>();
        if (count == 1) return new[] { start };

        var data = new float[count];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = start + i * step;
        }

        return data;
    }

    /// <summary>
    /// Calculates the mean of array elements with custom indexing.
    /// </summary>
    /// <param name="data">Source array.</param>
    /// <param name="skip">Number of elements to skip from the start.</param>
    /// <param name="step">Step size between elements to include.</param>
    /// <param name="length">Number of elements to include in the calculation.</param>
    /// <returns>Mean value of the selected elements.</returns>
    public static float Mean(in float[] data, int skip, int step, int length)
    {
        float sum = 0;
        if (length <= 0) return sum;

        for (int i = 0; i < length; ++i)
        {
            sum += data[skip + i * step];
        }
        return sum / length;
    }
}

#endregion

#region Model Processing

/// <summary>
/// Represents the BasicPitch ONNX model for audio-to-MIDI transcription.
/// </summary>
public class Model : IDisposable
{
    private InferenceSession session;
    private OutputName outputName;

    /// <summary>
    /// Initializes a new instance of the Model class and loads the embedded ONNX model.
    /// </summary>
    public Model()
    {
        // Load model synchronously as it's small
        var modelBytes = LoadModelBytes();
        session = new InferenceSession(modelBytes);
        // Model output names are fixed, can be pre-sorted
        outputName = new OutputName(session);
    }

    /// <summary>
    /// Performs audio-to-MIDI transcription prediction on the given audio buffer.
    /// </summary>
    /// <param name="waveBuffer">Audio data to process.</param>
    /// <param name="progressHandler">Optional progress callback.</param>
    /// <returns>Model output containing contours, notes, and onsets.</returns>
    public ModelOutput Predict(WaveBuffer waveBuffer, Action<double>? progressHandler = null)
    {
        var output = new ModelOutputHelper();

        // Iterate predictions
        var it = new ModelInput(waveBuffer, session.InputMetadata.First().Value);
        foreach (var (customTensor, progress) in it.Enumerate())
        {
            // Convert CustomTensor to ONNX Runtime tensor
            var onnxTensor = customTensor.ToOnnxTensor();

            // Single prediction
            var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor<float>(session.InputMetadata.First().Key, onnxTensor)
        };

            using var results = session.Run(inputs);

            // Get output results and convert
            var contourTensor = results.First(x => x.Name == outputName.Contour).AsTensor<float>();
            var noteTensor = results.First(x => x.Name == outputName.Note).AsTensor<float>();
            var onsetTensor = results.First(x => x.Name == outputName.Onset).AsTensor<float>();

            // Convert to custom Tensor type
            output.Contours.Add(ConvertToCustomTensor(contourTensor));
            output.Notes.Add(ConvertToCustomTensor(noteTensor));
            output.Onsets.Add(ConvertToCustomTensor(onsetTensor));

            // Progress callback
            progressHandler?.Invoke(progress);
        }

        // Convert collected results and return
        return output.Create(waveBuffer.FloatBufferCount);
    }

    /// <summary>
    /// Converts an ONNX Runtime tensor to a CustomTensor.
    /// </summary>
    /// <param name="onnxTensor">ONNX tensor to convert.</param>
    /// <returns>Converted CustomTensor.</returns>
    private static CustomTensor ConvertToCustomTensor(Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> onnxTensor)
    {
        // Extract data
        var data = onnxTensor.ToArray();

        // Convert shape
        var shape = new nint[onnxTensor.Dimensions.Length];
        for (int i = 0; i < onnxTensor.Dimensions.Length; i++)
        {
            shape[i] = onnxTensor.Dimensions[i];
        }

        return new CustomTensor(data, shape);
    }

    /// <summary>
    /// Loads the embedded ONNX model from resources.
    /// </summary>
    /// <returns>Model bytes as byte array.</returns>
    private static byte[] LoadModelBytes()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = "nmp.onnx";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(resourceName))
            {
                resourceName = name;
                break;
            }
        }

        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Releases all resources used by the Model.
    /// </summary>
    public void Dispose()
    {
        session?.Dispose();
    }
}

/// <summary>
/// Maps ONNX model output names to their corresponding semantic meanings.
/// </summary>
public class OutputName
{
    /// <summary>
    /// Name of the contour output tensor.
    /// </summary>
    public readonly string Contour;

    /// <summary>
    /// Name of the note output tensor.
    /// </summary>
    public readonly string Note;

    /// <summary>
    /// Name of the onset output tensor.
    /// </summary>
    public readonly string Onset;

    /// <summary>
    /// Initializes output names from the inference session metadata.
    /// </summary>
    /// <param name="session">ONNX inference session.</param>
    public OutputName(InferenceSession session)
    {
        var names = session.OutputMetadata.Keys.ToList();
        names.Sort();

        Contour = names[0];
        Note = names[1];
        Onset = names[2];
    }
}

/// <summary>
/// Helper class for collecting and organizing model output tensors.
/// </summary>
public class ModelOutputHelper
{
    /// <summary>
    /// Collection of contour tensors from model predictions.
    /// </summary>
    public readonly List<CustomTensor> Contours = new List<CustomTensor>();

    /// <summary>
    /// Collection of note tensors from model predictions.
    /// </summary>
    public readonly List<CustomTensor> Notes = new List<CustomTensor>();

    /// <summary>
    /// Collection of onset tensors from model predictions.
    /// </summary>
    public readonly List<CustomTensor> Onsets = new List<CustomTensor>();

    /// <summary>
    /// Creates a final ModelOutput by unwrapping and concatenating collected tensors.
    /// </summary>
    /// <param name="totalFrames">Total number of audio frames processed.</param>
    /// <returns>Consolidated ModelOutput.</returns>
    public ModelOutput Create(int totalFrames)
    {
        return new ModelOutput(Unwrap(Contours, totalFrames), Unwrap(Notes, totalFrames), Unwrap(Onsets, totalFrames));
    }

    /// <summary>
    /// Unwraps a collection of tensors into a single consolidated tensor.
    /// </summary>
    /// <param name="t">Collection of tensors to unwrap.</param>
    /// <param name="totalFrames">Total number of frames for proper sizing.</param>
    /// <returns>Consolidated tensor.</returns>
    private static Tensor Unwrap(IList<CustomTensor> t, int totalFrames)
    {
        if (t.Count == 0)
        {
            return new Tensor(null, null);
        }

#nullable disable
        var nOlap = Constants.N_OVERLAPPING_FRAMES / 2;
        var nOutputFramesOri = totalFrames * Constants.ANNOTATIONS_FPS / Constants.AUDIO_SAMPLE_RATE;
        var step = (int)t[0].Shape![t[0].Shape.Length - 1]; // Last dimension
        int[] oriShape = [t.Count, t[0].Data!.Length / step];
        var shape0 = Math.Min(oriShape[0] * oriShape[1] - nOlap * 2, nOutputFramesOri);
        var rangeStart = nOlap * step;
        var rangeCount = (oriShape[1] - nOlap) * step - rangeStart;
#nullable restore

        // Determine shape and required memory
        var shape = new nint[] { shape0, step };
        var data = new float[shape[0] * shape[1]];

        // Fill data
        int size = 0;
        foreach (var tensor in t)
        {
            var tensorData = tensor.Data!;
            var src = tensorData.AsSpan().Slice(rangeStart, Math.Min(rangeCount, tensorData.Length - rangeStart));

            foreach (var v in src)
            {
                if (size < data.Length)
                    data[size] = v;

                size += 1;
                if (size == data.Length)
                {
                    break;
                }
            }
        }
        return new Tensor(data, shape);
    }
}

/// <summary>
/// Handles input preparation and windowing for model inference.
/// </summary>
public class ModelInput
{
    private readonly WaveBuffer waveBuffer;
    private readonly ShapeHelper inputInfo;
    private float[] tensorData;

    /// <summary>
    /// Initializes a new ModelInput instance.
    /// </summary>
    /// <param name="waveBuffer">Audio data buffer.</param>
    /// <param name="metadata">Model input metadata.</param>
    public ModelInput(WaveBuffer waveBuffer, NodeMetadata metadata)
    {
        this.waveBuffer = waveBuffer;
        inputInfo = new ShapeHelper(metadata);
        tensorData = new float[inputInfo.Count];
    }

    /// <summary>
    /// Enumerates over audio windows, yielding tensors for model inference.
    /// </summary>
    /// <returns>Enumerable of (tensor, progress) tuples.</returns>
    public IEnumerable<(CustomTensor, Double)> Enumerate()
    {
        int cursor = Constants.OVERLAP_LEN / -2;
        int offset = -cursor;
        int totalFrames = waveBuffer.FloatBufferCount;

        int n, j;
        // Fill with 0 for cursor < 0 part
        tensorData.AsSpan().Slice(0, offset).Fill(0);

        while (cursor < totalFrames)
        {
            j = Math.Max(0, cursor);
            n = Math.Min(inputInfo.Count - offset, totalFrames - j);
            waveBuffer.FloatBuffer.AsSpan().Slice(j, n).CopyTo(tensorData.AsSpan().Slice(offset, n));
            offset += n;

            cursor += Constants.HOP_SIZE;

            if (offset == inputInfo.Count)
            {
                yield return CreateResult((double)cursor / (double)totalFrames);
            }
            else
            {
                // Last time, fill insufficient data with 0
                tensorData.AsSpan().Slice(offset).Fill(0);
                yield return CreateResult(1.0);
            }
            offset = 0;
        }
    }

    /// <summary>
    /// Creates a result tuple with tensor and progress information.
    /// </summary>
    /// <param name="progress">Processing progress (0.0 to 1.0).</param>
    /// <returns>Tuple containing CustomTensor and progress value.</returns>
    private (CustomTensor, Double) CreateResult(double progress)
    {
        // Create CustomTensor
        var data = tensorData.ToArray();
        var shape = inputInfo.Shape.Select(x => (nint)x).ToArray();
        var customTensor = new CustomTensor(data, shape);

        return (customTensor, Math.Clamp(progress, 0, 1));
    }
}

/// <summary>
/// Custom tensor implementation for interfacing with ONNX Runtime.
/// </summary>
public class CustomTensor
{
    /// <summary>
    /// Tensor data as float array.
    /// </summary>
    public readonly float[]? Data;

    /// <summary>
    /// Tensor shape dimensions.
    /// </summary>
    public readonly nint[]? Shape;

    /// <summary>
    /// Initializes a new CustomTensor instance.
    /// </summary>
    /// <param name="data">Tensor data.</param>
    /// <param name="shape">Tensor shape.</param>
    public CustomTensor(float[]? data, nint[]? shape)
    {
        Data = data;
        Shape = shape;
    }

    /// <summary>
    /// Converts this CustomTensor to an ONNX Runtime DenseTensor.
    /// </summary>
    /// <returns>ONNX Runtime DenseTensor.</returns>
    /// <exception cref="InvalidOperationException">Thrown when tensor data or shape is null.</exception>
    public Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> ToOnnxTensor()
    {
        if (Data == null || Shape == null)
            throw new InvalidOperationException("Cannot convert null tensor to ONNX tensor");

        // Convert shape to int array
        var intShape = Shape.Select(x => (int)x).ToArray();
        return new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(Data, intShape);
    }
}

/// <summary>
/// Helper class for handling tensor shape information from model metadata.
/// </summary>
public class ShapeHelper
{
    /// <summary>
    /// Tensor shape dimensions.
    /// </summary>
    public readonly int[] Shape;

    /// <summary>
    /// Total number of elements in the tensor.
    /// </summary>
    public readonly int Count = 1;

    /// <summary>
    /// Initializes shape information from node metadata.
    /// </summary>
    /// <param name="metadata">ONNX node metadata.</param>
    public ShapeHelper(NodeMetadata metadata)
    {
        var shape = metadata.Dimensions;

        Shape = new int[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            // abs to handle negative numbers
            var n = Math.Abs(shape[i]);
            Shape[i] = n;
            Count *= n;
        }
    }
}

#endregion

#region Note Representation

/// <summary>
/// Represents a musical note with timing, pitch, and amplitude information.
/// </summary>
public sealed class Note : IComparable<Note>
{
    /// <summary>
    /// Note start time in seconds.
    /// </summary>
    public readonly float StartTime;

    /// <summary>
    /// Note end time in seconds.
    /// </summary>
    public readonly float EndTime;

    /// <summary>
    /// MIDI pitch number (0-127).
    /// </summary>
    public readonly int Pitch;

    /// <summary>
    /// Note amplitude/velocity (0.0-1.0).
    /// </summary>
    public readonly float Amplitude;

    /// <summary>
    /// Optional pitch bend information over time.
    /// </summary>
    public float[]? PitchBend;

    /// <summary>
    /// Initializes a new Note instance.
    /// </summary>
    /// <param name="startTime">Start time in seconds.</param>
    /// <param name="endTime">End time in seconds.</param>
    /// <param name="pitch">MIDI pitch number.</param>
    /// <param name="amplitude">Note amplitude.</param>
    /// <param name="pitchBend">Optional pitch bend data.</param>
    public Note(float startTime, float endTime, int pitch, float amplitude, float[]? pitchBend)
    {
        StartTime = startTime;
        EndTime = endTime;
        Pitch = pitch;
        Amplitude = amplitude;
        PitchBend = pitchBend;
    }

    /// <summary>
    /// Returns a string representation of the note.
    /// </summary>
    /// <returns>Formatted string containing note information.</returns>
    public override string ToString()
    {
        var nbend = PitchBend != null ? PitchBend!.Length : 0;
        return $"start: {StartTime}, end: {EndTime}, pitch: {Pitch}, amplitude: {Amplitude}, bend: ${nbend}[{string.Join(",", PitchBend ?? [])}]";
    }

    /// <summary>
    /// Compares this note with another note for sorting purposes.
    /// </summary>
    /// <param name="other">Note to compare with.</param>
    /// <returns>Comparison result (-1, 0, or 1).</returns>
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
#endregion

#region MIDI Generation

/// <summary>
/// Manages MIDI file generation from detected musical notes.
/// </summary>
public static class MidiWriter
{
    /// <summary>
    /// Detected tempo in beats per minute (BPM).
    /// </summary>
    public static int DetectedTempo = 120;

    /// <summary>
    /// Generates a MIDI file from the detected notes
    /// 
    /// WHAT THIS FUNCTION DOES:
    /// - Takes our list of detected notes
    /// - Creates a standard MIDI file that music software can read
    /// - Converts timing from seconds to MIDI "ticks"
    /// - Creates "Note On" and "Note Off" events for each note
    /// 
    /// MIDI CONCEPTS:
    /// - MIDI = Musical Instrument Digital Interface
    /// - MIDI files don't contain audio, just instructions
    /// - Like sheet music for computers
    /// - Ticks = MIDI's way of measuring time (like frame rate)
    /// - Note On = start playing a note
    /// - Note Off = stop playing a note
    /// </summary>
    /// <param name="notes">List of detected notes</param>
    /// <param name="outputPath">Path for the output MIDI file</param>
    public static void GenerateMidiFile(List<Note> notes, string outputPath, int bpm = 120)
    {
        if (notes.Count > 10)
        {
            bpm = DetectTempo(notes);
            DetectedTempo = bpm;
        }

        Console.WriteLine($"Generating MIDI file with BPM: {bpm}");
        Console.WriteLine($"Number of notes: {notes.Count}");

        var midiFile = new MidiFile();  // The complete MIDI file
        var track = new TrackChunk();   // One track to hold all our notes

        // Create a list to hold all events with their absolute timing
        var timedEvents = new List<(long absoluteTime, MidiEvent midiEvent)>();

        // Set the tempo (speed) of the music
        // Convert BPM to microseconds per quarter note
        // Formula: 60,000,000 / BPM = microseconds per quarter note
        int microsecondsPerQuarterNote = 60_000_000 / bpm;
        var tempoEvent = new SetTempoEvent(microsecondsPerQuarterNote);
        timedEvents.Add((0, tempoEvent));  // Tempo event at time 0

        var programChangeEvent = new ProgramChangeEvent
        {
            Channel = (FourBitNumber)0,  // MIDI channel 0 (first channel)
            ProgramNumber = (SevenBitNumber)4 // Use the instrument (Rhodes Piano)
        };

        timedEvents.Add((0, programChangeEvent)); // Program change at time 0

        // Convert each detected note into MIDI events
        int noteIndex = 0;
        foreach (var note in notes)
        {
            // Convert time from seconds to MIDI ticks
            // 480 ticks per quarter note is a common standard
            // Calculate quarter notes per second based on actual BPM
            double quarterNotesPerSecond = bpm / 60.0;
            long startTicks = (long)(note.StartTime * 480 * quarterNotesPerSecond);
            long endTicks = (long)(note.EndTime * 480 * quarterNotesPerSecond);

            noteIndex++;

            // Create a "Note On" event (start playing the note)
            var noteOn = new NoteOnEvent(
                (SevenBitNumber)note.Pitch,  // Which note to play
                (SevenBitNumber)(int)(note.Amplitude * 100f) // How hard to play it (velocity)
            );

            // Create a "Note Off" event (stop playing the note)
            var noteOff = new NoteOffEvent(
                (SevenBitNumber)note.Pitch,  // Which note to stop
                (SevenBitNumber)0               // Release velocity (usually 0)
            );

            // Add both events with their absolute timing
            timedEvents.Add((startTicks, noteOn));
            timedEvents.Add((endTicks, noteOff));
        }

        // Sort all events by their absolute time
        timedEvents.Sort((a, b) => a.absoluteTime.CompareTo(b.absoluteTime));

        // Convert from absolute timing to relative timing and add to track
        // MIDI uses "delta time" = time since the previous event
        long previousTime = 0;
        foreach (var (absoluteTime, midiEvent) in timedEvents)
        {
            midiEvent.DeltaTime = absoluteTime - previousTime;  // Time since last event
            track.Events.Add(midiEvent);
            previousTime = absoluteTime;
        }

        // The library automatically adds an "End of Track" event when saving
        // This tells MIDI players that the song is finished

        // Add our track to the MIDI file
        midiFile.Chunks.Add(track);

        // Set the time division (ticks per quarter note)
        // This is CRITICAL for proper playback speed
        midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(480);

        // Save the complete MIDI file to disk
        // Delete existing file if it exists to avoid overwrite errors
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        midiFile.Write(outputPath);

        Console.WriteLine($"MIDI file saved: {outputPath}");
        Console.WriteLine($"Time division: 480 ticks per quarter note");
        Console.WriteLine($"Tempo: {bpm} BPM ({microsecondsPerQuarterNote} μs per quarter note)");
    }

    /// <summary>
    /// Detects the tempo of a list of musical notes based on their onset times.
    /// </summary>
    /// <param name="notes"></param>
    /// <returns></returns>
    public static int DetectTempo(List<Note> notes)
    {
        if (notes.Count < 2)
            return 120; // Default tempo

        // Get all note onset times
        var onsetTimes = notes.Select(n => n.StartTime).OrderBy(t => t).ToList();

        // Calculate intervals between consecutive onsets
        var intervals = new List<float>();
        for (int i = 1; i < onsetTimes.Count; i++)
        {
            float interval = onsetTimes[i] - onsetTimes[i - 1];
            if (interval > 0.05f && interval < 2.0f) // Filter out very short or very long intervals
            {
                intervals.Add(interval);
            }
        }

        if (intervals.Count == 0)
            return 120;

        // Find common beat intervals using histogram approach
        var beatCandidates = new Dictionary<int, int>(); // BPM -> count

        foreach (var interval in intervals)
        {
            // Test various beat divisions (quarter, eighth, sixteenth notes)
            for (int division = 1; division <= 4; division *= 2)
            {
                float beatInterval = interval * division;
                int bpm = (int)Math.Round(60.0f / beatInterval);

                // Only consider reasonable tempo range
                if (bpm >= 40 && bpm <= 200)
                {
                    // Allow some tolerance for tempo variations
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        int candidateBpm = bpm + offset;
                        if (candidateBpm >= 40 && candidateBpm <= 200)
                        {
                            if (!beatCandidates.ContainsKey(candidateBpm))
                                beatCandidates[candidateBpm] = 0;
                            beatCandidates[candidateBpm]++;
                        }
                    }
                }
            }
        }

        // Find the most common BPM
        if (beatCandidates.Count == 0)
            return 120;

        var detectedBpm = beatCandidates.OrderByDescending(kvp => kvp.Value).First().Key;

        // Prefer common tempos if they're close
        int[] commonTempos = { 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160 };
        foreach (var commonTempo in commonTempos)
        {
            if (Math.Abs(detectedBpm - commonTempo) <= 3)
            {
                //Console.WriteLine($"Tempo detection: {detectedBpm} BPM -> snapped to common tempo {commonTempo} BPM");
                return commonTempo;
            }
        }

        //Console.WriteLine($"Tempo detection: {detectedBpm} BPM (confidence: {beatCandidates[detectedBpm]} votes)");
        return detectedBpm;
    }
}
#endregion

#region Note Conversion

/// <summary>
/// Configuration options for converting model output to notes.
/// </summary>
public record struct NotesConvertOptions
{
    /// <summary>
    /// Onset threshold for note splitting and merging sensitivity (0.05-0.95, default: 0.5).
    /// </summary>
    public float OnsetThreshold = 0.5f;

    /// <summary>
    /// Frame threshold for note confidence filtering (0.05-0.95, default: 0.3).
    /// </summary>
    public float FrameThreshold = 0.3f;

    /// <summary>
    /// Minimum note length in frames (3-50, default: 11).
    /// </summary>
    public int MinNoteLength = 11;

    /// <summary>
    /// Energy threshold for note detection (default: 11).
    /// </summary>
    public int EnergyThreshold = 11;

    /// <summary>
    /// Minimum frequency limit in Hz (0-2000, default: null for no limit).
    /// </summary>
    public float? MinFreq = null;

    /// <summary>
    /// Maximum frequency limit in Hz (40-3000, default: null for no limit).
    /// </summary>
    public float? MaxFreq = null;

    /// <summary>
    /// Whether to infer onsets through post-processing (default: true).
    /// </summary>
    public bool InferOnsets = true;

    /// <summary>
    /// Whether to include pitch bend detection (default: true).
    /// </summary>
    public bool IncludePitchBends = true;

    /// <summary>
    /// Whether to use melodia trick for harmonic detection (default: true).
    /// </summary>
    public bool MelodiaTrick = true;

    /// <summary>
    /// Initializes default note conversion options.
    /// </summary>
    public NotesConvertOptions() { }
}

/// <summary>
/// Converts model output tensors to musical notes.
/// </summary>
public class NotesConverter
{
    private ModelOutput input;

    /// <summary>
    /// Initializes a new NotesConverter with model output.
    /// </summary>
    /// <param name="input">Model output containing contours, notes, and onsets.</param>
    public NotesConverter(ModelOutput input)
    {
        this.input = input;
    }

    /// <summary>
    /// Converts model output to a list of musical notes.
    /// </summary>
    /// <param name="opt">Conversion options.</param>
    /// <returns>List of detected notes.</returns>
    public List<Note> Convert(NotesConvertOptions opt)
    {
        var notes = ToNotesPolyphonic(opt);
        if (opt.IncludePitchBends)
        {
            GetPitchBend(ref notes);
        }
        return ToNoteList(notes);
    }

    /// <summary>
    /// Converts model recognition data to polyphonic notes.
    /// </summary>
    /// <param name="opt">Conversion options.</param>
    /// <returns>List of intermediate notes.</returns>
    private List<InterNote> ToNotesPolyphonic(NotesConvertOptions opt)
    {
        var (onsets, frames) = NotesHelper.ConstrainFrequency(input.Onsets, input.Notes, opt.MaxFreq, opt.MinFreq);
        if (opt.InferOnsets)
        {
            onsets = NotesHelper.GetInferedOnsets(onsets, frames);
        }

        var notes = new List<InterNote>();
        if (frames.Data == null)
        {
            return notes;
        }
        var remainingEnergy = new float[frames.Data.Length];
        var frameData = frames.Data!;
        frameData.CopyTo(remainingEnergy, 0);
        var onsetIdxs = NotesHelper.FindValidOnsetIndexs(onsets, opt.OnsetThreshold).Reverse();

        var frameStep = (int)frames.Shape![frames.Shape.Length - 1]; // Last dimension
        var nFrames = frames.Shape![0];
        var nFramesMinus1 = nFrames - 1;

        foreach (var idx in onsetIdxs)
        {
            var noteStartIdx = idx / frameStep;
            var freqIdx = idx % frameStep;

            if (noteStartIdx >= nFramesMinus1)
            {
                continue;
            }

            var i = noteStartIdx + 1;
            var k = 0;
            while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
            {
                if (remainingEnergy[i * frameStep + freqIdx] < opt.FrameThreshold)
                {
                    k += 1;
                }
                else
                {
                    k = 0;
                }
                i += 1;
            }

            i -= k;

            if (i - noteStartIdx <= opt.MinNoteLength)
            {
                continue;
            }

            // Clear submatrix
            // Calculate average of column data from idx to last row as amplitude
            // Using a loop here for performance optimization
            float amplitude = 0;
            for (var j = 0; j < (i - noteStartIdx); ++j)
            {
                var offset = idx + j * frameStep;
                amplitude += frameData[offset];
                remainingEnergy[offset] = 0;
                if (freqIdx < Constants.MAX_FREQ_IDX)
                {
                    remainingEnergy[offset + 1] = 0;
                }
                if (freqIdx > 0)
                {
                    remainingEnergy[offset - 1] = 0;
                }
            }
            amplitude /= (i - noteStartIdx);
            notes.Add(new InterNote(noteStartIdx, i, freqIdx + Constants.MIDI_OFFSET, amplitude));
        }

        if (opt.MelodiaTrick)
        {
            float maxValue = 0;
            int maxIdx = 0;
            float amplitude = 0;
            int i = 0;
            int k = 0;
            int startPos = 0;

            while (true)
            {
                maxIdx = TensorPrimitives.IndexOfMax(remainingEnergy);
                maxValue = remainingEnergy[maxIdx];
                if (maxValue <= opt.FrameThreshold)
                {
                    break;
                }

                var iMid = maxIdx / frameStep;
                var freqIdx = maxIdx % frameStep;
                remainingEnergy[iMid * frameStep + freqIdx] = 0;

                i = iMid + 1;
                k = 0;
                while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold)
                    {
                        k += 1;
                    }
                    else
                    {
                        k = 0;
                    }
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX)
                    {
                        remainingEnergy[startPos + 1] = 0;
                    }
                    if (freqIdx > 0)
                    {
                        remainingEnergy[startPos - 1] = 0;
                    }
                    i += 1;
                }
                var iEnd = i - 1 - k;

                i = iMid - 1;
                k = 0;
                while (i > 0 && k < opt.EnergyThreshold)
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold)
                    {
                        k += 1;
                    }
                    else
                    {
                        k = 0;
                    }
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX)
                    {
                        remainingEnergy[startPos + 1] = 0;
                    }
                    if (freqIdx > 0)
                    {
                        remainingEnergy[startPos - 1] = 0;
                    }
                    i -= 1;
                }
                var iStart = i + 1 + k;

                if (iStart < 0)
                {
                    throw new Exception($"iStart is: {iStart}");
                }
                if (iEnd >= nFrames)
                {
                    throw new Exception($"iEnd is: {iEnd}, nFrames is: {nFrames}");
                }

                var iLen = iEnd - iStart;
                if (iLen <= opt.MinNoteLength)
                {
                    continue;
                }

                amplitude = MathTool.Mean(frameData, iStart * frameStep + freqIdx, frameStep, iLen);
                notes.Add(new InterNote(iStart, iEnd, freqIdx + Constants.MIDI_OFFSET, amplitude));
            }
        }
        return notes;
    }

    /// <summary>
    /// Extracts pitch bend information for the detected notes.
    /// </summary>
    /// <param name="notes">List of notes to process (modified in place).</param>
    /// <param name="nBinsTolerance">Number of frequency bins tolerance for pitch bend detection (default: 25).</param>
    private void GetPitchBend(ref List<InterNote> notes, int nBinsTolerance = 25)
    {
        if (input.Contours.Data == null || notes.Count == 0) return;
        var contourSpan = input.Contours.Data!.AsSpan();
        var contourStep = (int)input.Contours.Shape![input.Contours.Shape.Length - 1]; // Last dimension

        var windowLen = nBinsTolerance * 2 + 1;
        var freqGaussianSpan = NotesHelper.MakeGaussianWindow(windowLen, 5).AsSpan();
        int freqIdx;
        int freqStartIdx;
        int freqEndIdx;
        int gaussianIdxStart;
        int gaussianIdxEnd;
        int cols;
        int rows;
        float pbShift;
        float maxValue = 0;
        int maxIdx = 0;
        int mulLength;

        var pitchBendSubMatrix = new float[Constants.N_FREQ_BINS_CONTOURS];
        var bends = new List<float>();
        foreach (InterNote note in notes)
        {
            freqIdx = (int)Math.Round(NotesHelper.MidiPitchToContourBin(note.Pitch));
            freqStartIdx = Math.Max(freqIdx - nBinsTolerance, 0);
            freqEndIdx = Math.Min(Constants.N_FREQ_BINS_CONTOURS, freqIdx + nBinsTolerance + 1);

            rows = note.IEndTime - note.IStartTime;
            cols = freqEndIdx - freqStartIdx;
            if (pitchBendSubMatrix.Length < cols)
            {
                pitchBendSubMatrix = new float[cols];
            }
            pitchBendSubMatrix.AsSpan().Fill(float.MinValue);

            // Gaussian vector
            gaussianIdxStart = Math.Max(nBinsTolerance - freqIdx, 0);
            gaussianIdxEnd = windowLen - Math.Max(freqIdx - (Constants.N_FREQ_BINS_CONTOURS - nBinsTolerance - 1), 0);
            if (gaussianIdxStart >= freqGaussianSpan.Length || gaussianIdxEnd > freqGaussianSpan.Length)
            {
                throw new Exception($"GetPitchBend failed, gaussian idx error: [{gaussianIdxStart},{gaussianIdxEnd}] {freqGaussianSpan.Length}");
            }

            // Split submatrix into row vectors and perform element-wise multiplication with gaussian
            bends.Clear();
            pbShift = -(float)(nBinsTolerance - Math.Max(0, nBinsTolerance - freqIdx));
            for (int i = 0; i < rows; ++i)
            {
                var start = (note.IStartTime + i) * contourStep + freqStartIdx;
                mulLength = Math.Min(cols, gaussianIdxEnd - gaussianIdxStart);
                var pstart = contourSpan.Slice(start, mulLength);
                var gaussianStart = freqGaussianSpan.Slice(gaussianIdxStart, mulLength);
                TensorPrimitives.Multiply(pstart, gaussianStart, pitchBendSubMatrix);
                // Calculate 1 bend
                maxIdx = TensorPrimitives.IndexOfMax(pitchBendSubMatrix.AsSpan().Slice(0, mulLength));
                maxValue = pitchBendSubMatrix[maxIdx];
                bends.Add((float)maxIdx);
            }
            if (bends.Count > 0)
            {
                note.PitchBend = bends.ToArray();
                TensorPrimitives.Add(note.PitchBend!, pbShift, note.PitchBend!);
            }
        }
    }

    /// <summary>
    /// Converts intermediate notes to final Note objects with proper timing.
    /// </summary>
    /// <param name="notes">List of intermediate notes.</param>
    /// <returns>List of final Note objects.</returns>
    private List<Note> ToNoteList(in List<InterNote> notes)
    {
        if (notes.Count == 0 || input.Contours.Shape == null)
        {
            // Return empty array
            return new List<Note>();
        }

        return notes.Select(i => new Note(
            NotesHelper.ModelFrameToTime(i.IStartTime),
            NotesHelper.ModelFrameToTime(i.IEndTime),
            i.Pitch,
            i.Amplitude,
            i.PitchBend
        )).ToList();
    }
}

#endregion

#region Helper Classes

/// <summary>
/// Provides utility functions for note processing and conversion.
/// </summary>
public class NotesHelper
{
    /// <summary>
    /// Converts frequency in Hz to MIDI note number.
    /// </summary>
    /// <param name="freq">Frequency in Hz.</param>
    /// <returns>MIDI note number (0-127).</returns>
    public static int HzToMidi(float freq)
    {
        return (int)Math.Round(12 * (Math.Log2(freq) - Math.Log2(440.0)) + 69);
    }

    /// <summary>
    /// Converts MIDI note number to frequency in Hz.
    /// </summary>
    /// <param name="pitch">MIDI note number.</param>
    /// <returns>Frequency in Hz.</returns>
    public static float MidiToHz(int pitch)
    {
        return (float)(Math.Pow(2, (pitch - 69) / 12f) * 440);
    }

    /// <summary>
    /// Converts model frame index to time in seconds.
    /// </summary>
    /// <param name="n">Frame index.</param>
    /// <returns>Time in seconds.</returns>
    public static float ModelFrameToTime(int n)
    {
        if (n < 1) return 0f;

        // Linear conversion from frame index to time in seconds
        return (n * Constants.FFT_HOP) / (float)Constants.AUDIO_SAMPLE_RATE;
    }

    /// <summary>
    /// Constrains frequency range by zeroing out frequencies outside specified limits.
    /// </summary>
    /// <param name="onsets">Onset tensor.</param>
    /// <param name="frames">Frame tensor.</param>
    /// <param name="maxFreq">Maximum frequency limit in Hz (null for no limit).</param>
    /// <param name="minFreq">Minimum frequency limit in Hz (null for no limit).</param>
    /// <returns>Tuple of constrained onset and frame tensors.</returns>
    public static (Tensor, Tensor) ConstrainFrequency(in Tensor onsets, in Tensor frames, float? maxFreq, float? minFreq)
    {
        if (maxFreq == null && minFreq == null)
        {
            return (onsets, frames);
        }

        // If max or min is set, need to copy memory
        var newOnsets = onsets.DeepClone();
        var newFrames = frames.DeepClone();

        if (maxFreq != null)
        {
            var pitch = HzToMidi(maxFreq.Value) - Constants.MIDI_OFFSET;
            var r = Range.StartAt(pitch);
            ZeroPitch(ref newOnsets, r);
            ZeroPitch(ref newFrames, r);
        }

        if (minFreq != null)
        {
            var pitch = HzToMidi(minFreq.Value) - Constants.MIDI_OFFSET;
            var r = Range.EndAt(pitch);
            ZeroPitch(ref newOnsets, r);
            ZeroPitch(ref newFrames, r);
        }

        return (newOnsets, newFrames);
    }

    /// <summary>
    /// Generates inferred onsets by analyzing frame differences.
    /// </summary>
    /// <param name="onsets">Original onset tensor.</param>
    /// <param name="frames">Frame tensor.</param>
    /// <param name="nDiff">Number of difference frames to analyze (default: 2).</param>
    /// <returns>Enhanced onset tensor with inferred onsets.</returns>
    public static Tensor GetInferedOnsets(in Tensor onsets, in Tensor frames, int nDiff = 2)
    {
        if (frames.Data == null)
        {
            return new Tensor(null, null);
        }

        // Calculate differences
        var frameData = frames.Data!;
        int frameSize = (int)frames.Shape![frames.Shape.Length - 1]; // Last dimension
        int totalFrameSize = frameData.Length;
        float[] diffs = new float[nDiff * totalFrameSize];
        var diffsSpan = diffs.AsSpan();
        for (int i = 0; i < nDiff; i++)
        {
            var start = i * totalFrameSize;
            var offset = frameSize * (i + 1);
            var length = Math.Max(totalFrameSize - offset, 0);
            if (length > 0)
            {
                Array.Copy(frameData, 0, diffs, start + offset, length);
            }
            var dest = diffsSpan.Slice(start, totalFrameSize);
            TensorPrimitives.Subtract(frameData, dest, dest);
        }

        // Find minimum of each column, store in first row of diffs. numpy: frame_diff = np.min(diffs, axis = 0)
        var frameDiff = diffsSpan.Slice(0, totalFrameSize);
        for (int i = 1; i < nDiff; i++)
        {
            TensorPrimitives.Min(diffsSpan.Slice(i * totalFrameSize, totalFrameSize), frameDiff, frameDiff);
        }

        // Set threshold. numpy: frame_diff[frame_diff < 0] = 0
        TensorPrimitives.Max(frameDiff, 0f, frameDiff);

        // numpy: frame_diff[:n_diff, :] = 0
        diffsSpan.Slice(0, nDiff * frameSize).Clear();

        // numpy: frame_diff = np.max(onsets) * frame_diff / np.max(frame_diff)
        var onsetData = onsets.Data!;
        {
            var maxDiff = TensorPrimitives.Max(frameDiff);
            float i = TensorPrimitives.Max(onsetData);
            if (maxDiff != 0f)
            {
                i = i / maxDiff;
            }
            var resultSpan = frameDiff;
            for (int j = 0; j < resultSpan.Length; j++)
            {
                resultSpan[j] = resultSpan[j] * i;
            }
        }

        // numpy: max_onsets_diff = np.max([onsets, frame_diff], axis = 0)
        float[] ret = new float[onsetData.Length];
        TensorPrimitives.Max(frameDiff, onsetData, ret);
        nint[] shape = new nint[onsets.Shape!.Length];
        onsets.Shape!.CopyTo(shape, 0);
        return new Tensor(ret, shape);
    }

    /// <summary>
    /// Finds valid onset indices that exceed the threshold and represent local maxima.
    /// </summary>
    /// <param name="onsets">Onset tensor to analyze.</param>
    /// <param name="threshold">Minimum threshold for valid onsets.</param>
    /// <returns>List of valid onset indices.</returns>
    public static IList<int> FindValidOnsetIndexs(in Tensor onsets, float threshold)
    {
        if (onsets.Shape![0] < 3)
        {
            return [];
        }

        // This algorithm finds peak positions in each column of the matrix (scipy.signal.argrelmax)
        // Filter found peaks through threshold to complete
        var data = onsets.Data!;
        float[] mask = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            mask[i] = Math.Min(data[i], threshold);
        }

        // Peaks can only be at middle positions, iteration only needs to cover [1, n - 2] 
        var step = (int)onsets.Shape![onsets.Shape.Length - 1]; // Last dimension
        var limit = mask.Length - step;
        float v;
        var ret = new List<int>();
        for (int i = step; i < limit; ++i)
        {
            if (mask[i] < threshold) continue;
            v = data[i];
            if ((v > data[i - step]) && (v > data[i + step]))
            {
                ret.Add(i);
            }
        }
        return ret;
    }

    /// <summary>
    /// Generates a Gaussian window function for signal processing.
    /// </summary>
    /// <param name="count">Number of samples in the window.</param>
    /// <param name="std">Standard deviation parameter.</param>
    /// <returns>Array containing the Gaussian window values.</returns>
    public static float[] MakeGaussianWindow(int count, int std)
    {
        if (count <= 0)
        {
            return [];
        }
        if (count == 1)
        {
            return [1.0f];
        }

        var n = MathTool.ARange(-0.5f * (count - 1), 1.0f, count);
        var sig2 = (float)(std * std * 2);

        TensorPrimitives.Multiply(n, n, n);
        TensorPrimitives.Divide(n, -sig2, n);
        TensorPrimitives.Exp(n, n);

        return n;
    }

    /// <summary>
    /// Converts MIDI pitch to contour frequency bin index.
    /// </summary>
    /// <param name="pitch">MIDI pitch number.</param>
    /// <returns>Corresponding contour bin index.</returns>
    public static float MidiPitchToContourBin(int pitch)
    {
        var hz = MidiToHz(pitch);
        return 12f * Constants.CONTOURS_BINS_PER_SEMITONE * (float)Math.Log2(hz / Constants.ANNOTATIONS_BASE_FREQUENCY);
    }

    /// <summary>
    /// Zeros out specific pitch ranges in a tensor.
    /// </summary>
    /// <param name="t">Tensor to modify (passed by reference).</param>
    /// <param name="pitchRange">Range of pitches to zero out.</param>
    private static void ZeroPitch(ref Tensor t, Range pitchRange)
    {
        if (t.Data == null) return;

        var limit = t.Shape![1];
        var l = pitchRange.Start.Value;
        if (l < 0 || l > limit) return;
        var r = pitchRange.End.Equals(Index.End) ? limit : pitchRange.End.Value;
        if (r < 0 || r > limit || r < l) return;

        // Manually clear the range since we don't have TensorSpan
        var step = (int)t.Shape![t.Shape.Length - 1];
        for (nint i = 0; i < t.Shape![0]; i++)
        {
            for (int j = l; j < r; j++)
            {
                t.Data![i * step + j] = 0;
            }
        }
    }
}

/// <summary>
/// Intermediate note representation used during processing before final Note conversion.
/// </summary>
/// <param name="IStartTime">Start time in model frames.</param>
/// <param name="IEndTime">End time in model frames.</param>
/// <param name="Pitch">MIDI pitch number.</param>
/// <param name="Amplitude">Note amplitude.</param>
/// <param name="PitchBend">Optional pitch bend data.</param>
record InterNote(int IStartTime, int IEndTime, int Pitch, float Amplitude, float[]? PitchBend = null)
{
    /// <summary>
    /// Gets or sets the pitch bend information for this note.
    /// </summary>
    public float[]? PitchBend { get; set; } = PitchBend;
}

#endregion

#region Data Structures

/// <summary>
/// Generic tensor implementation for handling multi-dimensional float arrays.
/// </summary>
public class Tensor
{
    /// <summary>
    /// Tensor data as a flat float array.
    /// </summary>
    public readonly float[]? Data;

    /// <summary>
    /// Tensor shape dimensions.
    /// </summary>
    public readonly nint[]? Shape;

    /// <summary>
    /// Initializes a new Tensor instance.
    /// </summary>
    /// <param name="data">Tensor data array.</param>
    /// <param name="shape">Shape dimensions.</param>
    public Tensor(float[]? data, nint[]? shape)
    {
        Data = data;
        Shape = shape;
    }

    /// <summary>
    /// Creates a deep copy of this tensor.
    /// </summary>
    /// <returns>New tensor with copied data and shape.</returns>
    public Tensor DeepClone()
    {
        float[]? data = null;
        nint[]? shape = null;

        if (Data != null)
        {
            data = new float[Data.Length];
            Data.CopyTo(data, 0);
        }

        if (Shape != null)
        {
            shape = new nint[Shape.Length];
            Shape.CopyTo(shape, 0);
        }

        return new Tensor(data, shape);
    }
}

/// <summary>
/// Contains the complete output from the BasicPitch model prediction.
/// </summary>
public class ModelOutput
{
    /// <summary>
    /// Contour tensor containing pitch contour information.
    /// </summary>
    public readonly Tensor Contours;

    /// <summary>
    /// Note tensor containing note activation information.
    /// </summary>
    public readonly Tensor Notes;

    /// <summary>
    /// Onset tensor containing note onset information.
    /// </summary>
    public readonly Tensor Onsets;

    /// <summary>
    /// Initializes a new ModelOutput instance.
    /// </summary>
    /// <param name="c">Contour tensor.</param>
    /// <param name="n">Note tensor.</param>
    /// <param name="o">Onset tensor.</param>
    public ModelOutput(Tensor c, Tensor n, Tensor o)
    {
        Contours = c;
        Notes = n;
        Onsets = o;
    }
}

/// <summary>
/// Represents a buffer for storing wave data as floating-point values.
/// </summary>
public class WaveBuffer
{
    /// <summary>
    /// Gets or sets the number of elements in the float buffer.
    /// </summary>
    public int FloatBufferCount { get; set; }

    /// <summary>
    /// Gets or sets the array of floating-point values representing the wave data.
    /// </summary>
    public float[]? FloatBuffer { get; set; }

    /// <summary>
    /// Initializes a new instance of the WaveBuffer class.
    /// </summary>
    public WaveBuffer() { }

    /// <summary>
    /// Initializes a new instance of the WaveBuffer class with the specified float array.
    /// </summary>
    /// <param name="_buffer">The float array to initialize the buffer with.</param>
    public WaveBuffer(float[] _buffer)
    {
        FloatBuffer = _buffer;
        FloatBufferCount = _buffer.Length;
    }

    /// <summary>
    /// Initializes a new instance of the WaveBuffer class with the specified span of floats.
    /// </summary>
    /// <param name="_buffer">The span of floats to initialize the buffer with.</param>
    public WaveBuffer(Span<float> _buffer)
    {
        FloatBufferCount = _buffer.Length;
        FloatBuffer = _buffer.ToArray();
    }
}
#endregion
