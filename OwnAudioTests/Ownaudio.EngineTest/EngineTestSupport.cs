using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;

namespace Ownaudio.EngineTest;

/// <summary>
/// Shared helpers for the engine-layer tests after the Rust cut-over.
/// </summary>
internal static class EngineTestSupport
{
    /// <summary>
    /// Creates an engine for the given configuration, or marks the test inconclusive when the host
    /// has no audio device able to open that configuration (for example, no microphone permission on
    /// this platform, or an input device that does not support the requested format). The Rust engine
    /// correctly reports the device limitation; whether a suitable device exists is environmental, so
    /// such cases are skipped rather than failed. On a host with the device available (e.g. Windows CI
    /// with microphone access) the engine is created and the test runs for real.
    /// </summary>
    public static IAudioEngine CreateOrSkip(AudioConfig config)
    {
        try
        {
            return AudioEngineFactory.Create(config);
        }
        catch (Exception ex) when (IsDeviceUnavailable(ex))
        {
            Assert.Inconclusive(
                $"Audio device unavailable for this configuration on this host (environmental, not an engine defect): {ex.Message}");
            throw; // unreachable — Assert.Inconclusive stops the test.
        }
    }

    /// <summary>
    /// Returns true when the exception (or any inner exception) indicates the audio device could not
    /// open the requested stream configuration, i.e. an environmental limitation rather than a bug.
    /// </summary>
    private static bool IsDeviceUnavailable(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            string message = e.Message ?? string.Empty;
            if (message.Contains("not supported by device", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("stream configuration", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no default", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no audio device", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
