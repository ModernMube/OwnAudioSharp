using FluentAssertions;
using OwnaudioNET.Sources;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// The managed OutputRoute value type: defensive copies, length checks and the value comparison
/// the mixer's change detection leans on. What the route does to the audio is the Rust core's
/// business and is tested there.
/// </summary>
public class OutputRouteTests
{
    [Fact]
    public void Ctor_CopiesTheArrays_SoALaterEditCannotSneakThrough()
    {
        int[] _map = { 0, 1 };
        float[] _gains = { 1.0f, 0.5f };
        var route = new OutputRoute(_map, _gains);

        _map[1] = 3;
        _gains[1] = 0.1f;

        route.SourceForChannel.Should().Equal(0, 1);
        route.Gains.Should().Equal(1.0f, 0.5f);
    }

    [Fact]
    public void Ctor_GainLengthMismatch_Throws()
    {
        Action act = () => new OutputRoute(new[] { 0, 1 }, new[] { 1.0f });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_NullMap_Throws()
    {
        Action act = () => new OutputRoute(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Gains_AreOptional()
    {
        var route = new OutputRoute(new[] { 0, -1, 0 });

        route.Gains.Should().BeNull();
        route.SourceForChannel.Should().Equal(0, -1, 0);
    }

    [Fact]
    public void Identity_MapsChannelToItself()
    {
        OutputRoute.Identity(4).SourceForChannel.Should().Equal(0, 1, 2, 3);
        OutputRoute.Identity(0).SourceForChannel.Should().BeEmpty();
    }

    [Fact]
    public void Equal_ComparesByValue()
    {
        var a = new OutputRoute(new[] { 0, 1 }, new[] { 1.0f, 0.5f });
        var b = new OutputRoute(new[] { 0, 1 }, new[] { 1.0f, 0.5f });
        var differentGain = new OutputRoute(new[] { 0, 1 }, new[] { 1.0f, 0.25f });
        var noGain = new OutputRoute(new[] { 0, 1 });

        OutputRoute.Equal(a, b).Should().BeTrue();
        OutputRoute.Equal(a, differentGain).Should().BeFalse();
        OutputRoute.Equal(a, noGain).Should().BeFalse();
        OutputRoute.Equal(null, null).Should().BeTrue();
        OutputRoute.Equal(a, null).Should().BeFalse();
    }

    [Fact]
    public void Equal_SpotsADifferentMap()
    {
        var a = new OutputRoute(new[] { 0, 1 });
        var b = new OutputRoute(new[] { 1, 0 });
        var shorter = new OutputRoute(new[] { 0 });

        OutputRoute.Equal(a, b).Should().BeFalse();
        OutputRoute.Equal(a, shorter).Should().BeFalse();
    }
}
