namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Shape of the synthetic value generated for a channel while
    /// <see cref="TelemetrySender.TestMode"/> is on. Resolved once per channel
    /// at dashboard-load time by <see cref="TestSignalCatalog.Resolve"/> and
    /// stored on <see cref="Dashboard.ChannelDefinition.TestSignal"/>.
    /// </summary>
    public enum TestKind
    {
        /// <summary>Triangle wave between <c>Min</c> and <c>Max</c> with period <c>PeriodMs</c>.</summary>
        Sweep,
        /// <summary>Fixed <c>Constant</c>.</summary>
        Constant,
        /// <summary>Integer ping-pong through <c>[Min..Max]</c> stepping every <c>StepMs</c>.</summary>
        Step,
        /// <summary>Alternates <c>Min</c>/<c>Max</c> every <c>StepMs</c>.</summary>
        Toggle,
        /// <summary>Monotonic counter wrapping <c>Min..Max</c> every <c>StepMs</c>.</summary>
        Increment,
        /// <summary>String placeholder; numeric path returns 0 (V2 frame layout skips string-typed channels).</summary>
        StringConstant,
        /// <summary>
        /// Wall-clock elapsed seconds since <see cref="TestSignalGenerator.ResetEpoch"/>
        /// was last called (i.e. since TestMode was switched on). Starts at 0
        /// and counts up monotonically for the lifetime of the test session.
        /// <c>Min</c> is added as a starting offset, <c>Max</c> is interpreted
        /// as a per-second multiplier (default 1.0 if zero) so the same kind
        /// can represent minute/hour-scaled counters too.
        /// </summary>
        Elapsed,
    }

    /// <summary>
    /// Per-channel test-mode value descriptor. Built by <see cref="TestSignalCatalog"/>;
    /// consumed by <see cref="TestSignalGenerator.Compute"/> on every test-frame tick.
    /// </summary>
    public struct TestSignal
    {
        public TestKind Kind;
        /// <summary>Lower bound for Sweep / Step / Toggle / Increment.</summary>
        public double Min;
        /// <summary>Upper bound for Sweep / Step / Toggle / Increment.</summary>
        public double Max;
        /// <summary>Returned value when Kind == Constant.</summary>
        public double Constant;
        /// <summary>Full triangle period for Sweep, in milliseconds. Default 5000.</summary>
        public int PeriodMs;
        /// <summary>Dwell time per discrete state for Step / Toggle / Increment, in milliseconds.</summary>
        public int StepMs;
        /// <summary>Quantise the result to an integer (Step). Booleans implicitly already integer.</summary>
        public bool StepIsInt;
        /// <summary>Per-channel phase offset so identical signals don't synchronize on the wire.</summary>
        public int PhaseOffsetMs;
        /// <summary>String payload when Kind == StringConstant. Dormant on V2 frame wire.</summary>
        public string? StringValue;
        /// <summary>
        /// For Increment: when false, the counter clamps at <c>Max</c> once
        /// reached and stays there for the rest of the test. Default true =
        /// wrap back to <c>Min</c>. Used for lap counters that should hit a
        /// final lap and stop.
        /// </summary>
        public bool Wrap;

        public static TestSignal Sweep(double min, double max, int periodMs = 5000, int phaseOffsetMs = 0)
            => new TestSignal { Kind = TestKind.Sweep, Min = min, Max = max, PeriodMs = periodMs, PhaseOffsetMs = phaseOffsetMs, StringValue = "" };

        public static TestSignal Constant_(double value)
            => new TestSignal { Kind = TestKind.Constant, Constant = value, StringValue = "" };

        public static TestSignal Step(double min, double max, int stepMs, bool isInt = true, int phaseOffsetMs = 0)
            => new TestSignal { Kind = TestKind.Step, Min = min, Max = max, StepMs = stepMs, StepIsInt = isInt, PhaseOffsetMs = phaseOffsetMs, StringValue = "" };

        public static TestSignal Toggle(int stepMs = 4000, int phaseOffsetMs = 0)
            => new TestSignal { Kind = TestKind.Toggle, Min = 0, Max = 1, StepMs = stepMs, StepIsInt = true, PhaseOffsetMs = phaseOffsetMs, StringValue = "" };

        public static TestSignal Increment(double min, double max, int stepMs, int phaseOffsetMs = 0, bool wrap = true)
            => new TestSignal { Kind = TestKind.Increment, Min = min, Max = max, StepMs = stepMs, StepIsInt = true, PhaseOffsetMs = phaseOffsetMs, StringValue = "", Wrap = wrap };

        public static TestSignal StringConstant_(string value)
            => new TestSignal { Kind = TestKind.StringConstant, StringValue = value ?? "" };

        /// <summary>
        /// Monotonically counts seconds since TestMode was switched on.
        /// <paramref name="startOffset"/> is added (default 0 = start at 00:00:00);
        /// <paramref name="perSecond"/> scales the rate (default 1 = real-time seconds).
        /// </summary>
        public static TestSignal Elapsed(double startOffset = 0.0, double perSecond = 1.0)
            => new TestSignal { Kind = TestKind.Elapsed, Min = startOffset, Max = perSecond, StringValue = "" };
    }
}
