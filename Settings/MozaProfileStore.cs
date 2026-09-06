using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Manages Moza device profiles using SimHub's native profile system.
    /// Handles per-game profile switching, profile creation/deletion, and persistence.
    /// </summary>
    public class MozaProfileStore : ProfileSettingsBase<MozaProfile, MozaProfileStore>, IProfileSettings<MozaProfile>, IProfileSettings
    {
        public override string FileFilter => "Moza profile (*.shmozaprofile)|*.shmozaprofile";

        public override void InitProfile(MozaProfile p)
        {
            // No special initialization needed for deserialized profiles
        }
    }
}
