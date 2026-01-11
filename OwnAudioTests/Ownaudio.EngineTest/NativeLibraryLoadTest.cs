using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Decoders;
using System;
using System.IO;

namespace Ownaudio.EngineTest;

[TestClass]
public class NativeLibraryLoadTest
{
    [TestMethod]
    public void TestMiniAudioLibraryLoad()
    {
        try
        {
            // Step 1: Load Ownaudio.Native assembly
            Console.WriteLine("Step 1: Loading Ownaudio.Native assembly...");
            var assembly = System.Reflection.Assembly.Load("Ownaudio.Native");
            Console.WriteLine($"✓ Loaded assembly: {assembly.FullName}");

            var decoderType = assembly.GetType("Ownaudio.Native.Decoders.MaDecoder");
            Console.WriteLine($"✓ Found MaDecoder type: {decoderType?.FullName}");

            // Step 2: Try to find a test file (MP3 is better supported)
            Console.WriteLine("\nStep 2: Looking for test audio file...");
            string testFile = @"E:\VisualStudioProjects\Multiplatform\Ownaudio\OwnAudio\OwnaudioExamples\OwnaudioDesktopExample\media\drums.mp3";

            if (!File.Exists(testFile))
            {
                Console.WriteLine($"! Test file not found: {testFile}");
                // Try WAV instead
                testFile = @"E:\VisualStudioProjects\Multiplatform\Ownaudio\OwnAudio\OwnaudioExamples\OwnaudioDesktopExample\media\drums.wav";
            }

            if (File.Exists(testFile))
            {
                Console.WriteLine($"✓ Using test file: {testFile}");

                // Step 3: Try to create decoder instance
                Console.WriteLine("\nStep 3: Creating MaDecoder instance...");
                var decoder = Activator.CreateInstance(decoderType!, testFile, 48000, 2) as IAudioDecoder;
                Console.WriteLine($"✓ Created MaDecoder instance!");
                Console.WriteLine($"  Sample Rate: {decoder?.StreamInfo.SampleRate}");
                Console.WriteLine($"  Channels: {decoder?.StreamInfo.Channels}");
                Console.WriteLine($"  Duration: {decoder?.StreamInfo.Duration}");

                // Step 4: Try to decode a frame
                Console.WriteLine("\nStep 4: Attempting to decode a frame...");
                var result = decoder?.DecodeNextFrame();
                if (result.HasValue)
                {
                    Console.WriteLine($"✓ Decoded frame result:");
                    Console.WriteLine($"  IsSucceeded: {result.Value.IsSucceeded}");
                    Console.WriteLine($"  IsEOF: {result.Value.IsEOF}");
                    Console.WriteLine($"  Has Frame: {result.Value.Frame != null}");
                    if (!string.IsNullOrEmpty(result.Value.ErrorMessage))
                    {
                        Console.WriteLine($"  Error: {result.Value.ErrorMessage}");
                    }
                }
                else
                {
                    Console.WriteLine($"! Decode returned null");
                }

                (decoder as IDisposable)?.Dispose();
                Console.WriteLine($"\n✓ MiniAudio decoder test COMPLETED SUCCESSFULLY");
            }
            else
            {
                Console.WriteLine($"! No audio file available for testing");
                Console.WriteLine($"  Skipping decoder creation test");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Test FAILED: {ex.GetType().Name}");
            Console.WriteLine($"  Message: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner Exception: {ex.InnerException.GetType().Name}");
                Console.WriteLine($"  Inner Message: {ex.InnerException.Message}");
                if (ex.InnerException.InnerException != null)
                {
                    Console.WriteLine($"  Inner Inner Message: {ex.InnerException.InnerException.Message}");
                }
            }

            throw;
        }
    }
}
