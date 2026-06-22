using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Safe;
using Ownaudio.Safe.Exceptions;

namespace Ownaudio.EngineTest;

/// <summary>
/// Integration tests for the Rust-backed <see cref="StreamingAudioDecoder"/>.
/// Each test writes a temporary 16-bit PCM WAV file to disk (the native decoder
/// opens files by path) and verifies decode, seek, EOF and lifecycle behaviour.
/// </summary>
[TestClass]
public class StreamingAudioDecoderTests
{
    private const int SampleRate = 44_100;

    [TestMethod]
    public void Open_ReportsStreamInfo()
    {
        using var wav = new TempWavFile(channels: 2, SampleRate, frames: 4_000);
        using var decoder = new StreamingAudioDecoder(wav.Path);

        Assert.AreEqual(2, decoder.StreamInfo.Channels, "channels");
        Assert.AreEqual(SampleRate, decoder.StreamInfo.SampleRate, "sample rate");
        Assert.IsTrue(decoder.StreamInfo.HasKnownDuration, "duration should be known for WAV");
        Assert.IsTrue(decoder.StreamInfo.Duration > TimeSpan.Zero, "positive duration");
    }

    [TestMethod]
    public void Read_DrainsEntireStream()
    {
        const int frames = 20_000;
        using var wav = new TempWavFile(channels: 1, SampleRate, frames);
        using var decoder = new StreamingAudioDecoder(wav.Path);

        var buffer = new float[2048];
        long total = 0;
        for (int i = 0; i < 5_000; i++)
        {
            int n = decoder.Read(buffer, 0, buffer.Length);
            total += n;
            if (n == 0)
            {
                if (decoder.IsEndOfStream)
                {
                    break;
                }
                Thread.Sleep(2);
            }
        }

        Assert.IsTrue(decoder.IsEndOfStream, "should reach end of stream");
        Assert.AreEqual(frames, total, "streamed sample count should equal source frames");
    }

    [TestMethod]
    public void Seek_RepositionsStream()
    {
        const int frames = 12_000;
        // value == frame index so a decoded sample maps uniquely back to its frame.
        using var wav = new TempWavFile(channels: 1, SampleRate, frames, linear: true);
        using var decoder = new StreamingAudioDecoder(wav.Path);

        const long target = 6_000;
        decoder.Seek(target);

        var buffer = new float[64];
        float? first = null;
        for (int i = 0; i < 500; i++)
        {
            int n = decoder.Read(buffer, 0, buffer.Length);
            if (n > 0)
            {
                first = buffer[0];
                break;
            }
            Thread.Sleep(2);
        }

        Assert.IsTrue(first.HasValue, "should read samples after seek");
        long landed = (long)Math.Round(first!.Value * 32768.0);
        Assert.IsTrue(Math.Abs(landed - target) <= 64, $"post-seek landed at {landed}, expected {target}");
    }

    [TestMethod]
    public void TargetChannels_UpmixesMonoToStereo()
    {
        using var wav = new TempWavFile(channels: 1, SampleRate, frames: 2_000);
        using var decoder = new StreamingAudioDecoder(wav.Path, targetChannels: 2);

        Assert.AreEqual(2, decoder.StreamInfo.Channels);
    }

    [TestMethod]
    public void Open_MissingFile_Throws()
    {
        Assert.ThrowsExactly<DecoderException>(
            () => new StreamingAudioDecoder("/no/such/file/anywhere.wav"));
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        using var wav = new TempWavFile(channels: 2, SampleRate, frames: 1_000);
        var decoder = new StreamingAudioDecoder(wav.Path);
        decoder.Dispose();
        decoder.Dispose();
    }

    [TestMethod]
    public void Read_AfterDispose_Throws()
    {
        using var wav = new TempWavFile(channels: 1, SampleRate, frames: 1_000);
        var decoder = new StreamingAudioDecoder(wav.Path);
        decoder.Dispose();

        var buffer = new float[64];
        Assert.ThrowsExactly<ObjectDisposedException>(() => decoder.Read(buffer, 0, buffer.Length));
    }

    #region Helpers

    /// <summary>
    /// Writes a temporary 16-bit PCM WAV file and removes it on dispose.
    /// </summary>
    private sealed class TempWavFile : IDisposable
    {
        public string Path { get; }

        public TempWavFile(int channels, int sampleRate, int frames, bool linear = false)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ownaudio_cs_test_{Guid.NewGuid():N}.wav");

            int dataLen = frames * channels * 2;
            int byteRate = sampleRate * channels * 2;
            short blockAlign = (short)(channels * 2);

            using var ms = new FileStream(Path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(ms);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((ushort)blockAlign);
            w.Write((ushort)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataLen);

            for (int i = 0; i < frames; i++)
            {
                short value = linear ? (short)i : (short)((i % 1000) * 30);
                for (int c = 0; c < channels; c++)
                {
                    w.Write(value);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    #endregion
}
