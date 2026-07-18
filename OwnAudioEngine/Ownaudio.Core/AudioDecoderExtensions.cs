using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Decoders;

namespace Ownaudio.Core;

/// <summary>
/// Handy bits bolted onto IAudioDecoder.
/// </summary>
public static class AudioDecoderExtensions
{
    /// <summary>
    /// Pulls the whole thing into one float array. Cold path, so the List growth is fine;
    /// the read loop itself stays on the zero-alloc ReadFrames.
    /// </summary>
    /// <returns>A float array containing all the decoded audio samples.</returns>
    public static float[] ReadAllSamples(this IAudioDecoder decoder)
    {
        var info = decoder.StreamInfo;
        int guess = (int)(info.Duration.TotalSeconds * info.SampleRate * info.Channels);
        var all = new List<float>(guess > 0 ? guess : 65536);

        var buffer = new byte[8192 * info.Channels * sizeof(float)];

        while (true)
        {
            var result = decoder.ReadFrames(buffer);

            if (result.IsSucceeded && result.FramesRead > 0)
            {
                int bytes = result.FramesRead * info.Channels * sizeof(float);
                all.AddRange(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes)));
            }

            if (result.IsEOF) break;
        }

        return all.ToArray();
    }
}
