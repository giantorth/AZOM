namespace MozaPlugin.Settings
{
    /// <summary>
    /// HMA150 Motion Effects sliders for one profile, in percent (0..100); -1 = no opinion,
    /// the controller keeps what it holds. Index order: pitch, roll, heave, acceleration
    /// pitch, acceleration roll (<c>MozaMotionProtocol.EffectSlots</c>), then road detail.
    /// </summary>
    public sealed class MotionEffectsSettings
    {
        public const int Count = 6;
        public const int RoadDetailIndex = 5;

        public int PitchIntensity { get; set; } = -1;
        public int PitchSmoothness { get; set; } = -1;
        public int RollIntensity { get; set; } = -1;
        public int RollSmoothness { get; set; } = -1;
        public int HeaveIntensity { get; set; } = -1;
        public int HeaveSmoothness { get; set; } = -1;
        public int AccelPitchIntensity { get; set; } = -1;
        public int AccelPitchSmoothness { get; set; } = -1;
        public int AccelRollIntensity { get; set; } = -1;
        public int AccelRollSmoothness { get; set; } = -1;
        public int RoadDetailIntensity { get; set; } = -1;
        public int RoadDetailSmoothness { get; set; } = -1;

        public (int Intensity, int Smoothness) Get(int index) => index switch
        {
            0 => (PitchIntensity, PitchSmoothness),
            1 => (RollIntensity, RollSmoothness),
            2 => (HeaveIntensity, HeaveSmoothness),
            3 => (AccelPitchIntensity, AccelPitchSmoothness),
            4 => (AccelRollIntensity, AccelRollSmoothness),
            _ => (RoadDetailIntensity, RoadDetailSmoothness),
        };

        public void Set(int index, int intensity, int smoothness)
        {
            switch (index)
            {
                case 0: PitchIntensity = intensity; PitchSmoothness = smoothness; break;
                case 1: RollIntensity = intensity; RollSmoothness = smoothness; break;
                case 2: HeaveIntensity = intensity; HeaveSmoothness = smoothness; break;
                case 3: AccelPitchIntensity = intensity; AccelPitchSmoothness = smoothness; break;
                case 4: AccelRollIntensity = intensity; AccelRollSmoothness = smoothness; break;
                default: RoadDetailIntensity = intensity; RoadDetailSmoothness = smoothness; break;
            }
        }

        public MotionEffectsSettings Clone() => (MotionEffectsSettings)MemberwiseClone();
    }
}
