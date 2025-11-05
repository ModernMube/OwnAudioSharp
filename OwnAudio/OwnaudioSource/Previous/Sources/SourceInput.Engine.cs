using Ownaudio.Core;
using System;

namespace OwnaudioLegacy.Sources;

public partial class SourceInput : ISource
{
    /// <summary>
    /// We retrieve the input data
    /// </summary>
    /// <param name="recData"></param>
    /// <param name="Engine"></param>
    public void ReceivesData(out float[] recData, IAudioEngine? Engine)
   {
        if (Engine == null)
        {
            recData = Array.Empty<float>();
            return;
        }

        Engine.Receives(out var inputBuffer);

        if (inputBuffer != null && inputBuffer.Length > 0 && Engine.OwnAudioEngineStopped() == 0)
        {
            ProcessSampleProcessors(inputBuffer);
        }

        recData = inputBuffer ?? Array.Empty<float>();
    }
}
