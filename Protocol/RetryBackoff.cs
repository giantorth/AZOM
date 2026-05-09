namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Retry-backoff schedules shared between the V0 (TelemetrySender) and V2
    /// (MozaTelemetryHost) telemetry pipelines. Centralising prevents drift
    /// when one pipeline's cadence is tuned and the other is forgotten.
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
