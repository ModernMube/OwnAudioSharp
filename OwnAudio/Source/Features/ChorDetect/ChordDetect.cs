using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using OwnaudioNET.Exceptions;
using Ownaudio.Decoders;
using Ownaudio.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;

namespace OwnaudioNET.Features.OwnChordDetect
{
    public static class ChordDetect
    {
        public static (List<TimedChord>, MusicalKey, int) DetectFromFile(
            string audioFile,
            float intervalSecond = 1.0f)
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
                /* Handle progress updates if needed */
                Console.Write($"\rRecognizing musical notes: {progress:P1}");
            });
            Console.WriteLine(" ");

            //Fine-tuning musical note recognition
            var convertOptions = new NotesConvertOptions
            {
                OnsetThreshold = 0.5f,      // Sound onset sensitivity
                FrameThreshold = 0.2f,      // Sound detection threshold
                MinNoteLength = 15,         // Minimum sound length (ms)
                MinFreq = 90f,              // Min frequency (Hz)
                MaxFreq = 2800f,            // Max frequency (Hz)
                IncludePitchBends = false,   // Pitch bend detection
                MelodiaTrick = true      // Harmonic detection
            };

            var converter = new NotesConverter(modelOutput);
            List<OwnaudioNET.Features.Extensions.Note> rawNotes = converter.Convert(convertOptions);

            int detectTempo = MidiWriter.DetectTempo(rawNotes);

            //Fine - tuning musical chord recognition
            var analyzer = new SongChordAnalyzer(
                    windowSize: intervalSecond,        // 1 second windows
                    hopSize: 0.5f,           // 0.25 steps per second
                    minimumChordDuration: 1.0f, // Min 1.0 second chord
                    confidence: 0.90f       // Minimum 90% reliability
                );

            var chords = analyzer.AnalyzeSong(rawNotes);
            MusicalKey? detectedKey = analyzer.DetectedKey;

            _decoder.Dispose();
#nullable disable
            return (chords, detectedKey, detectTempo);
#nullable restore
        }

        public static (string chord, float stability) DetectRealtime(
            List<Note> notes,
            DetectionMode mode = DetectionMode.Optimized,
            int buffersize = 5)

        {
            
            var realtimeDetector = new RealTimeChordDetector(buffersize, mode);
            
            if (realtimeDetector != null)
                return realtimeDetector.ProcessNotes(notes);
            else
                return ("", 0.0f);
        }
    }
}
