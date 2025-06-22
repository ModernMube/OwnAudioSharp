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
        Engine.Receives(out var inputBuffer);

        if (inputBuffer.Length > 0 && Engine.OwnAudioEngineStopped() == 0)
        {
            ProcessSampleProcessors(inputBuffer);
        }

        recData = inputBuffer;
    }
}
