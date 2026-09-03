namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Retry-backoff schedules for the telemetry pipeline. Centralised so
    /// blind-retransmit cadences are tuned in one place.
    /// </summary>
    public static class RetryBackoff
    {
        // Tier-def blind retransmit. PitHouse capture (2026-05-02) shows
        // typical absorption within 3 rounds; 6 covers slow-firmware tail.
        // Total budget ≈ 4 s. Early-exit fires as soon as the wheel acks
        // by sending catalog activity.
        public static readonly int[] TierDefBlindMs =
            { 100, 200, 400, 700, 1100, 1500 };
    }
}
