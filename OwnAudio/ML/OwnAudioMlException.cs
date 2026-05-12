namespace OwnAudio.ML;

/// <summary>Thrown when the ownaudio_ml native library returns an error.</summary>
public sealed class OwnAudioMlException : Exception
{
    public OwnAudioMlException(string message) : base(message) { }
    public OwnAudioMlException(string message, Exception inner) : base(message, inner) { }
}
