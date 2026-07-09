using System;
using FluentAssertions;
using OwnaudioNET.Engine;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Engine;

/// <summary>
/// Unit tests for <see cref="RustNativeChain"/> — the internal gate reporting that the Rust-native
/// file-playback chain is the exclusive production path (plan L — legacy cut-over). Verifies that
/// the chain defaults to enabled and that the internal <see cref="RustNativeChain.Override"/> test
/// hook is honored in both directions.
/// </summary>
/// <remarks>
/// The override is process-global, so every test restores it on dispose. The class is a single
/// non-parallel collection so the shared global state is not raced. The former
/// <see cref="AppContext"/> switch and environment-variable opt-outs were removed with the legacy
/// managed path and are no longer tested.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class RustNativeChainTests : IDisposable
{
    private readonly bool? _priorOverride;

    /// <summary>
    /// Captures the ambient override so each test starts from a known clean state (override
    /// cleared) and can be restored on dispose.
    /// </summary>
    public RustNativeChainTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = null;
    }

    /// <summary>
    /// Restores the override captured in the constructor.
    /// </summary>
    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
    }

    /// <summary>
    /// With no override, the chain defaults to enabled (the exclusive Rust-native path, as of 4.0).
    /// </summary>
    [Fact]
    public void Default_IsEnabled()
    {
        RustNativeChain.Enabled.Should().BeTrue();
    }

    /// <summary>
    /// An explicit <see cref="RustNativeChain.Override"/> is honored in both directions.
    /// </summary>
    /// <param name="value">The override value under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Override_IsHonored(bool value)
    {
        RustNativeChain.Override = value;
        RustNativeChain.Enabled.Should().Be(value);
    }
}
