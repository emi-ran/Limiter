namespace Limiter;

internal static class RateMath
{
    // A virtual finish time paces packets across all flows of one application.
    internal static long Due(long now, long previousDue, int packetBytes, int limitKBps, long ticksPerSecond)
    {
        if (packetBytes <= 0 || limitKBps <= 0 || ticksPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(packetBytes));
        long interval = (long)Math.Ceiling((double)packetBytes * ticksPerSecond / (limitKBps * 1024.0));
        return checked(Math.Max(now, previousDue) + Math.Max(1, interval));
    }
}
