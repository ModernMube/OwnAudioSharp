using System;
using FluentAssertions;
using OwnaudioNET.Engine;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Engine;

/// <summary>
/// D.2.a (plan 14 / WS2) unit tests for <see cref="RustNativeChain"/> — the internal opt-in gate
/// that selects the Rust-native file-playback chain without adding any public API surface.
/// Verifies the resolution order (<see cref="RustNativeChain.Override"/> wins over the
/// <see cref="AppContext"/> switch, which wins over the environment-variable fallback) and the
/// accepted truthy environment values.
/// </summary>
/// <remarks>
/// The override, the <see cref="AppContext"/> switch and the environment variable are all
/// process-global, so every test restores the override and the environment variable it touched.
/// The class is a single non-parallel collection so the shared global state is not raced.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class RustNativeChainTests : IDisposable
{
    private readonly bool? _priorOverride;
    private readonly string? _priorEnv;

    /// <summary>
    /// Captures the ambient override and environment variable so each test starts from a known
    /// clean state (override cleared, environment variable removed) and can be restored on dispose.
    /// </summary>
    public RustNativeChainTests()
    {
        _priorOverride = RustNativeChain.Override;
        _priorEnv = Environment.GetEnvironmentVariable(RustNativeChain.EnvironmentVariableName);

        RustNativeChain.Override = null;
        Environment.SetEnvironmentVariable(RustNativeChain.EnvironmentVariableName, null);
    }

    /// <summary>
    /// Restores the override and environment variable captured in the constructor.
    /// </summary>
    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        Environment.SetEnvironmentVariable(RustNativeChain.EnvironmentVariableName, _priorEnv);
    }

    /// <summary>
    /// With no override and no environment variable, the chain defaults to enabled (Rust-native
    /// path, as of 4.0 / plan 14 D.4).
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

    /// <summary>
    /// With the override unset, truthy environment values enable and falsy ones disable the chain;
    /// unset or unrecognized values fall back to the Rust-native default (4.0 / plan 14 D.4).
    /// </summary>
    /// <param name="value">The environment-variable value under test.</param>
    /// <param name="expected">The expected resolved <see cref="RustNativeChain.Enabled"/> value.</param>
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("On", true)]
    [InlineData(" yes ", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("Off", false)]
    [InlineData(" no ", false)]
    [InlineData("", true)]
    [InlineData("nonsense", true)]
    public void EnvironmentVariable_ResolvesWhenOverrideUnset(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(RustNativeChain.EnvironmentVariableName, value);
        RustNativeChain.Enabled.Should().Be(expected);
    }

    /// <summary>
    /// An explicit override takes precedence over a conflicting environment variable.
    /// </summary>
    [Fact]
    public void Override_TakesPrecedenceOverEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(RustNativeChain.EnvironmentVariableName, "1");
        RustNativeChain.Override = false;

        RustNativeChain.Enabled.Should().BeFalse();
    }
}
