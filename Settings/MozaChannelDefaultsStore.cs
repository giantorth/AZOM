using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// The master channel mapper's own profile store — a second, independent
    /// <see cref="ProfileSettingsBase{TProfile,TSettings}"/> alongside
    /// <see cref="MozaProfileStore"/>.
    ///
    /// <para>Being a real SimHub store (not a homegrown named list) it gets the whole
    /// profile machinery for free: the master mapper's <c>ProfileList</c> renders the
    /// dropdown plus New / Clone / Edit / Manage, <c>Init()</c> picks the profile
    /// matching the running game, <c>CurrentProfileChanged</c> reports switches from
    /// either source, and the manager dialog handles import/export and the switching
    /// mode. None of it touches the device-profile store.</para>
    /// </summary>
    public class MozaChannelDefaultsStore
        : ProfileSettingsBase<MozaChannelDefaultsProfile, MozaChannelDefaultsStore>,
          IProfileSettings<MozaChannelDefaultsProfile>, IProfileSettings
    {
        // Distinct extension from the device store's .shmozaprofile so the two can't
        // be imported into each other.
        public override string FileFilter
            => "Moza channel defaults (*.shmozachannels)|*.shmozachannels";

        public override void InitProfile(MozaChannelDefaultsProfile p)
        {
            // No special initialization needed for deserialized profiles
        }
    }
}
