namespace NpuChat;

internal enum BreathLevel { Normal, NearLimit, ReserveExceeded }

internal sealed record ContextBreath(int Capacity, int Used, bool Estimated)
{
    internal int Remaining => Math.Max(0, Capacity - Used);
    internal int Reserve => Math.Clamp(Capacity / 32, 16, 128);
    internal double Percent => Math.Clamp(100d * Used / Capacity, 0, 100);
    internal BreathLevel Level => !Estimated && Capacity - Used < Reserve ? BreathLevel.ReserveExceeded :
        Remaining <= Math.Max(Reserve, Capacity / 5) ? BreathLevel.NearLimit : BreathLevel.Normal;

    internal static ContextBreath? Tightest(IEnumerable<ContextBreath?> readings) => readings.OfType<ContextBreath>()
        .OrderByDescending(reading => reading.Level).ThenByDescending(reading => reading.Percent).FirstOrDefault();
}
