namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// The mixer's command queue was full and the call changed nothing — safe to retry once the
/// audio thread has rendered a block. Without a running output nothing drains it.
/// </summary>
public sealed class CommandQueueFullException : OwnAudioException
{
    /// <param name="message"></param>
    public CommandQueueFullException(string message)
        : base(AudioEngineErrorCode.CommandQueueFull, message) { }
}
