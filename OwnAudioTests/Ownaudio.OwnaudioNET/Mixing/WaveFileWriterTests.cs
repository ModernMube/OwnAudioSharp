using System.Text;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// WaveFileWriter: the header it lays down, the samples it streams, and the "bounce a source to
/// WAV" loop the docs hand out — pinned to the legacy chain so ReadSamples decodes here rather
/// than natively.
/// </summary>
[Collection("RustNativeChain")]
public class WaveFileWriterTests : IDisposable
{
    private readonly bool? _priorOverride;
    private readonly string _path;
    private readonly AudioConfig _config;

    public WaveFileWriterTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = false;

        _path = Path.Combine(Path.GetTempPath(), $"ownaudio-wav-{Guid.NewGuid():N}.wav");
        _config = new AudioConfig { SampleRate = 48000, Channels = 2, BufferSize = 512 };
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void WritesAFloat32WavWithAPatchedHeader()
    {
        const int frames = 1000;

        using (var wav = new WaveFileWriter(_path, _config))
        {
            float[] block = new float[frames * _config.Channels];
            for (int i = 0; i < block.Length; i++) block[i] = (i % 200) / 200f - 0.5f;

            wav.WriteSamples(block);

            wav.TotalFramesWritten.Should().Be(frames);
            wav.Duration.Should().BeApproximately(frames / 48000.0, 1e-9);
        }

        byte[] file = File.ReadAllBytes(_path);
        int dataBytes = frames * _config.Channels * sizeof(float);

        file.Length.Should().Be(44 + dataBytes);
        Encoding.ASCII.GetString(file, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(file, 8, 4).Should().Be("WAVE");
        BitConverter.ToInt32(file, 4).Should().Be(file.Length - 8, "the RIFF size is patched on Dispose");

        // fmt chunk: format tag 3 (IEEE float), 2ch, 48k, 32 bits
        BitConverter.ToUInt16(file, 20).Should().Be(3);
        BitConverter.ToUInt16(file, 22).Should().Be(2);
        BitConverter.ToInt32(file, 24).Should().Be(48000);
        BitConverter.ToUInt16(file, 34).Should().Be(32);

        Encoding.ASCII.GetString(file, 36, 4).Should().Be("data");
        BitConverter.ToInt32(file, 40).Should().Be(dataBytes, "the data size is patched on Dispose");
    }

    [Fact]
    public void BouncesASourceToWav_TheWayTheDocsShowIt()
    {
        // Exactly the loop documented under "Writing a WAV yourself": play the source, pull
        // frames, hand them to the writer, close the file to finalize the header.
        const int frames = 4800;
        float[] samples = new float[frames * _config.Channels];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.25f;

        var source = new SampleSource(samples, _config);
        source.Play();

        using (var wav = new WaveFileWriter(_path, source.Config))
        {
            float[] buffer = new float[4096 * source.Config.Channels];

            while (!source.IsEndOfStream)
            {
                int read = source.ReadSamples(buffer, 4096);
                if (read <= 0) break;

                wav.WriteSamples(buffer.AsSpan(0, read * source.Config.Channels));
            }
        }

        source.Dispose();

        long written = (new FileInfo(_path).Length - 44) / (_config.Channels * sizeof(float));
        written.Should().BeGreaterThanOrEqualTo(frames, "the whole source has to reach the file");
    }

    [Fact]
    public void WriteAfterDispose_Throws()
    {
        var wav = new WaveFileWriter(_path, _config);
        wav.Dispose();

        Action act = () => wav.WriteSamples(new float[] { 0f, 0f });

        act.Should().Throw<ObjectDisposedException>();
    }
}
