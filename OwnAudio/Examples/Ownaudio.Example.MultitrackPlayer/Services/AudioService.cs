using System;
using System.Threading.Tasks;
using Logger;
using OwnaudioNET;
using OwnaudioNET.Events;
using OwnaudioNET.Mixing;

namespace MultitrackPlayer.Services;

/// <summary>
/// Owns the engine and the one mixer on it. Everything else talks to the audio stack through here.
/// </summary>
public sealed class AudioService : IDisposable
{
    private static AudioService? _instance;
    private static readonly object _lock = new object();

    private AudioMixer? _mixer;
    private bool _disposed;

    private AudioService() { }

    public static AudioService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock) { _instance ??= new AudioService(); }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Null until InitializeAsync ran.
    /// </summary>
    public AudioMixer? Mixer => _mixer;

    public bool IsInitialized => _mixer != null && OwnaudioNet.IsInitialized;

    /// <summary>
    /// Native output died - unplug, sleep/wake, rate change. Raised off the mixer's control thread.
    /// </summary>
    public event EventHandler<AudioStreamFaultEventArgs>? StreamFaulted;

    /// <summary>
    /// Every source ran out. Off the native end-of-stream latch, not the UI thread either.
    /// </summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// Opens the engine on the default device and starts a mixer on it. A second call does nothing.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsInitialized) return;

        var config = OwnaudioNet.CreateDefaultConfig();
        await OwnaudioNet.InitializeAsync(config);
        OwnaudioNet.Start();

        _mixer = new AudioMixer(OwnaudioNet.Engine!.UnderlyingEngine, bufferSizeInFrames: config.BufferSize);
        _mixer.StreamFaulted += _onStreamFaulted;
        _mixer.PlaybackEnded += _onPlaybackEnded;
        _mixer.Start();
    }

    private void _onStreamFaulted(object? sender, AudioStreamFaultEventArgs e) => StreamFaulted?.Invoke(this, e);

    private void _onPlaybackEnded(object? sender, EventArgs e) => PlaybackEnded?.Invoke(this, e);

    /// <summary>
    /// Mixer first, then the engine. Each step on its own, a faulted device throws from the
    /// mixer's dispose and that must not skip the engine shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (_mixer != null)
        {
            _mixer.StreamFaulted -= _onStreamFaulted;
            _mixer.PlaybackEnded -= _onPlaybackEnded;

            _step("Mixer stop", _mixer.Stop);
            _step("Mixer dispose", _mixer.Dispose);
            _mixer = null;
        }

        if (OwnaudioNet.IsInitialized)
        {
            _step("Engine stop", OwnaudioNet.Stop);
            _step("Engine shutdown", OwnaudioNet.Shutdown);
        }

        _disposed = true;
    }

    private static void _step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error($"[AudioService] {what} threw, carrying on", ex); }
    }
}
