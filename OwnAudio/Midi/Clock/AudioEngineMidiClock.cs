namespace OwnAudio.Midi.Clock;

/// <summary>
/// 24 PPQN clock driven off the render loop instead of a system thread — call
/// ProcessAudioBlock once per callback and the pulses land sample accurate,
/// no OS scheduler jitter.
/// </summary>
public sealed class AudioEngineMidiClock
{
    /// <summary>
    /// How far we are toward the next pulse, in samples.
    /// </summary>
    private double _sampleCounter;

    /// <summary>
    /// Samples between two pulses at the current tempo / rate.
    /// </summary>
    private double _samplesPerPulse;

    /// <summary>
    /// Recalculates the pulse spacing. Call it before the first block, and again
    /// whenever tempo or sample rate moves.
    /// </summary>
    public void UpdateTempo(double bpm, int sampleRate)
    {
        _samplesPerPulse = sampleRate / ((bpm * 24.0) / 60.0);
    }

    /// <summary>
    /// Steps the clock by one render block and fires 0xF8 for every pulse that
    /// falls inside it. Audio thread only.
    /// </summary>
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
