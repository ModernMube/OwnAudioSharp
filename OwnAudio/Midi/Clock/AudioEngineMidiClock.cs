namespace OwnAudio.Midi.Clock;

/// <summary>
/// Sample-accurate MIDI clock that derives 24 PPQN timing pulses from the audio render loop.
/// Instead of running a dedicated system thread, this clock is driven by calling
/// <see cref="ProcessAudioBlock"/> once per audio render callback with the block size.
/// This eliminates jitter caused by OS thread scheduling.
/// </summary>
public sealed class AudioEngineMidiClock
{
    /// <summary>
    /// Fractional sample counter tracking progress toward the next timing pulse.
    /// </summary>
    private double _sampleCounter;

    /// <summary>
    /// Number of audio samples per MIDI timing clock pulse at the current tempo and sample rate.
    /// </summary>
    private double _samplesPerPulse;

    /// <summary>
    /// Updates the internal timing parameters when the tempo or sample rate changes.
    /// Must be called before <see cref="ProcessAudioBlock"/> to produce correct output.
    /// </summary>
    /// <param name="bpm">
    /// Tempo in beats per minute. Must be greater than zero.
    /// </param>
    /// <param name="sampleRate">
    /// Audio engine sample rate in Hz (e.g. 44100 or 48000).
    /// </param>
    public void UpdateTempo(double bpm, int sampleRate)
    {
        double pulsesPerSec = (bpm * 24.0) / 60.0;
        _samplesPerPulse = sampleRate / pulsesPerSec;
    }

    /// <summary>
    /// Advances the clock by <paramref name="sampleCount"/> samples and sends 0xF8 Timing Clock
    /// messages to <paramref name="output"/> for each pulse boundary that falls within the block.
    /// Call this method once per audio render callback from the audio processing thread.
    /// </summary>
    /// <param name="sampleCount">
    /// Number of samples in the current audio render block (e.g. 256 or 512).
    /// </param>
    /// <param name="output">
    /// The MIDI output port that receives the 0xF8 Timing Clock messages.
    /// </param>
    public void ProcessAudioBlock(int sampleCount, IO.IMidiOutputPort output)
    {
        if (_samplesPerPulse <= 0) return;
        _sampleCounter += sampleCount;
        while (_sampleCounter >= _samplesPerPulse)
        {
            output.Send(new IO.MidiMessage(0xF8, 0, 0));
            _sampleCounter -= _samplesPerPulse;
        }
    }
}
