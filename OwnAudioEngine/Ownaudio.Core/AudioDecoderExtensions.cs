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
        int estimatedSamples = (int)(decoder.StreamInfo.Duration.TotalSeconds
            * decoder.StreamInfo.SampleRate
            * decoder.StreamInfo.Channels);
        var allSamples = new List<float>(estimatedSamples > 0 ? estimatedSamples : 65536);

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
                allSamples.AddRange(floatSpan);
            }

            if (result.IsEOF)
                break;
        }
        return allSamples.ToArray();
    }
}
