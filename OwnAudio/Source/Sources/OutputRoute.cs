using System;

namespace OwnaudioNET.Sources;

/// <summary>
/// Destination indexed output routing for one source: mix bus channel dst takes the source's
/// channel SourceForChannel[dst], at Gains[dst]. -1 means that bus channel gets nothing from us.
/// Two destinations may name the same source channel — that fan out (one mono click onto two
/// physical outputs) is what OutputChannelMapping, being source indexed, can't express.
/// </summary>
public sealed class OutputRoute
{
    /// <summary>
    /// Source channel per bus channel, -1 for unbound. Copied in, so a later edit of the caller's
    /// array doesn't sneak past the mixer's change detection.
    /// </summary>
    public int[] SourceForChannel { get; }

    /// <summary>
    /// Linear gain per bus channel, null for unity throughout.
    /// </summary>
    public float[]? Gains { get; }

    /// <summary>
    /// Route the given bus channels, optionally with a per channel gain of the same length.
    /// </summary>
    /// <param name="sourceForChannel">source channel per bus channel, -1 for unbound</param>
    /// <param name="gains"></param>
    public OutputRoute(int[] sourceForChannel, float[]? gains = null)
    {
        ArgumentNullException.ThrowIfNull(sourceForChannel);

        if (gains != null && gains.Length != sourceForChannel.Length)
            throw new ArgumentException(
                $"Gain count ({gains.Length}) must match the route length ({sourceForChannel.Length}).", nameof(gains));

        SourceForChannel = (int[])sourceForChannel.Clone();
        Gains = gains is null ? null : (float[])gains.Clone();
    }

    /// <summary>
    /// Straight identity route over the first channelCount bus channels, i to i.
    /// </summary>
    /// <param name="channelCount"></param>
    public static OutputRoute Identity(int channelCount)
    {
        int[] _map = new int[Math.Max(0, channelCount)];
        for (int i = 0; i < _map.Length; i++) _map[i] = i;
        return new OutputRoute(_map);
    }

    /// <summary>
    /// Value comparison, either side may be null.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    public static bool Equal(OutputRoute? a, OutputRoute? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (!a.SourceForChannel.AsSpan().SequenceEqual(b.SourceForChannel)) return false;

        if (a.Gains is null || b.Gains is null) return a.Gains is null && b.Gains is null;
        return a.Gains.AsSpan().SequenceEqual(b.Gains);
    }
}
