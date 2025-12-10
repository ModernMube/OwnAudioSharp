using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Decoders;

namespace Ownaudio.Core;

/// <summary>
/// Provides efficient extension methods for IAudioDecoder.
/// </summary>
public static class AudioDecoderExtensions
{
    /// <summary>
    /// Reads all audio samples from a decoder into a single float array.
    /// This method is highly efficient and uses the zero-allocation ReadFrames method in a loop.
    /// It replaces the old, inefficient DecodeAllFrames method.
    /// </summary>
    /// <param name="decoder">The decoder to read from.</param>
    /// <returns>A float array containing all the decoded audio samples.</returns>
    public static float[] ReadAllSamples(this IAudioDecoder decoder)
    {
        // Use a List<float> to accumulate samples. It's more efficient than MemoryStream for this.
        var allSamples = new List<float>((int)(decoder.StreamInfo.Duration.TotalSeconds * decoder.StreamInfo.SampleRate));
        
        // Create a reusable buffer. Its size should be based on what FileSource uses. 8192 frames is a good default.
        int framesPerBuffer = 8192;
        int bufferSizeInBytes = framesPerBuffer * decoder.StreamInfo.Channels * sizeof(float);
        var buffer = new byte[bufferSizeInBytes];

        while (true)
        {
            var result = decoder.ReadFrames(buffer);

            if (result.IsSucceeded && result.FramesRead > 0)
            {
                int bytesRead = result.FramesRead * decoder.StreamInfo.Channels * sizeof(float);
                var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRead));
                allSamples.AddRange(floatSpan.ToArray());
            }

            if (result.IsEOF)
            {
                break;
            }
        }
        return allSamples.ToArray();
    }
}
