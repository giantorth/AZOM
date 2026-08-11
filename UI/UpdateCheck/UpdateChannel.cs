namespace MozaPlugin.UI.UpdateCheck
{
    /// <summary>
    /// Legacy release-stream enum, persisted as int in
    /// <see cref="MozaPluginSettings.UpdateChannel"/>. Superseded by the
    /// string channel id (<see cref="MozaPluginSettings.UpdateChannelId"/>);
    /// kept only so older builds reading a newer settings blob stay valid.
    /// </summary>
    public enum UpdateChannel
    {
        Stable = 0,
        Dev = 1,
    }
}
