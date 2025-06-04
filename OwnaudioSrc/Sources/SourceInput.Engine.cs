using Ownaudio.Engines;

namespace Ownaudio.Sources;

public partial class SourceInput : ISource
{
    /// <summary>
    /// We retrieve the input data
    /// </summary>
    /// <param name="recData"></param>
    /// <param name="Engine"></param>
    public void ReceivesData(out float[] recData, IAudioEngine Engine)
   {
        float[] inputBuffer = new float[Engine.FramesPerBuffer * (int)_inputoptions.Channels];

        if (Engine.OwnAudioEngineStopped() == 0)
        {
            Engine.Receives(out inputBuffer);

            if (inputBuffer.Length > 0)
            {
                ProcessSampleProcessors(inputBuffer);
            }
        }

        recData = inputBuffer;
    }
}
