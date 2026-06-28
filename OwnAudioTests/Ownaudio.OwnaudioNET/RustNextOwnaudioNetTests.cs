using System;
using System.Threading.Tasks;
using FluentAssertions;
using Ownaudio.Core;
using Xunit;
using RustNet = OwnaudioNET.RustNext.OwnaudioNet;

namespace Ownaudio.OwnaudioNET.Tests;

/// <summary>
/// Tests for the phase-3 Rust-backed clone entry point <see cref="OwnaudioNET.RustNext.OwnaudioNet"/>.
/// The mock engine path is exercised so the clone's plumbing (init → start → send → receive → stop
/// → shutdown) is verified without audio hardware, mirroring the original
/// <see cref="OwnaudioNET.OwnaudioNet"/> behaviour.
/// </summary>
public class RustNextOwnaudioNetTests : IDisposable
{
    public RustNextOwnaudioNetTests()
    {
        if (RustNet.IsInitialized)
        {
            RustNet.Shutdown();
        }
    }

    public void Dispose()
    {
        if (RustNet.IsInitialized)
        {
            RustNet.Shutdown();
        }
    }

    [Fact]
    public void Initialize_WithMockEngine_ShouldSetIsInitialized()
    {
        RustNet.Initialize(RustNet.CreateDefaultConfig(), useMockEngine: true);

        RustNet.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public void Initialize_WithNullConfig_ShouldThrowArgumentNullException()
    {
        Action act = () => RustNet.Initialize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Initialize_CalledTwice_ShouldNotThrow()
    {
        var config = RustNet.CreateDefaultConfig();

        RustNet.Initialize(config, useMockEngine: true);
        Action act = () => RustNet.Initialize(config, useMockEngine: true);

        act.Should().NotThrow();
        RustNet.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public void Start_BeforeInitialize_ShouldThrowInvalidOperationException()
    {
        Action act = RustNet.Start;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void StartStop_WithMockEngine_ShouldToggleIsRunning()
    {
        RustNet.Initialize(RustNet.CreateDefaultConfig(), useMockEngine: true);

        RustNet.Start();
        RustNet.IsRunning.Should().BeTrue();

        RustNet.Stop();
        RustNet.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Send_BeforeInitialize_ShouldThrowInvalidOperationException()
    {
        Action act = () => RustNet.Send(new float[256]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Send_WhileRunning_ShouldNotThrow()
    {
        var config = RustNet.CreateDefaultConfig();
        RustNet.Initialize(config, useMockEngine: true);
        RustNet.Start();

        float[] samples = new float[config.BufferSize * config.Channels];
        Action act = () => RustNet.Send(samples);

        act.Should().NotThrow();
    }

    [Fact]
    public void Lifecycle_FullSequence_ShouldLeaveLibraryUninitialized()
    {
        var config = RustNet.CreateDefaultConfig();

        RustNet.Initialize(config, useMockEngine: true);
        RustNet.Start();
        RustNet.Send(new float[config.BufferSize * config.Channels]);
        RustNet.Stop();
        RustNet.Shutdown();

        RustNet.IsInitialized.Should().BeFalse();
        RustNet.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void GetOutputDevices_BeforeInitialize_ShouldThrowInvalidOperationException()
    {
        Action act = () => RustNet.GetOutputDevices();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateLowLatencyConfig_ShouldUseSmallBuffer()
    {
        AudioConfig config = RustNet.CreateLowLatencyConfig();

        config.BufferSize.Should().Be(128);
        config.SampleRate.Should().Be(48000);
        config.Channels.Should().Be(2);
    }

    [Fact]
    public async Task InitializeAsync_WithMockEngine_ShouldInitialize()
    {
        await RustNet.InitializeAsync(RustNet.CreateDefaultConfig(), useMockEngine: true);

        RustNet.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public void Version_ShouldMatchPublicSurface()
    {
        RustNet.Version.Should().Be(new Version(2, 6, 7));
    }
}
