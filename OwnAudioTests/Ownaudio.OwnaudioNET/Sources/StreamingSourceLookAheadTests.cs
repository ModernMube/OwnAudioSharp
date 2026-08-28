using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Sources;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// The pump's look-ahead. It is the latency of anything played into a StreamingSource live,
/// so it has to be settable — and clamped, because under the floor the feed starves instead
/// of getting faster.
/// </summary>
public class StreamingSourceLookAheadTests : IDisposable
{
    private readonly AudioConfig _config = new AudioConfig
    {
        SampleRate = 48000,
        Channels = 2,
        BufferSize = 512
    };

    private StreamingSource? _source;

    private StreamingSource _make()
        => _source = new StreamingSource((buffer, frames, position) => buffer.Clear(), _config);

    public void Dispose() => _source?.Dispose();

    [Fact]
    public void DefaultsToTheScheduledLatency()
    {
        Assert.Equal(StreamingSource.DefaultLookAheadSeconds, _make().LookAheadSeconds, 4);
    }

    [Fact]
    public void ATighterLookAheadIsKept()
    {
        var source = _make();

        source.LookAheadSeconds = 0.01;

        Assert.Equal(0.01, source.LookAheadSeconds, 4);
    }

    [Fact]
    public void BelowTheFloorItClamps()
    {
        var source = _make();

        source.LookAheadSeconds = 0.0001;

        Assert.Equal(StreamingSource.MinLookAheadSeconds, source.LookAheadSeconds, 4);
    }

    [Fact]
    public void AboveTheCeilingItClamps()
    {
        var source = _make();

        source.LookAheadSeconds = 30.0;

        Assert.Equal(StreamingSource.MaxLookAheadSeconds, source.LookAheadSeconds, 4);
    }

    /// <summary>
    /// Negatives and NaN are what a slider hands over when something upstream goes wrong;
    /// neither may end up as a zero-length feed target.
    /// </summary>
    [Fact]
    public void NonsenseStillLeavesAUsableTarget()
    {
        var source = _make();

        source.LookAheadSeconds = -1.0;
        Assert.True(source.LookAheadSeconds >= StreamingSource.MinLookAheadSeconds);

        source.LookAheadSeconds = double.NaN;
        Assert.True(source.LookAheadSeconds > 0.0);
    }
}
