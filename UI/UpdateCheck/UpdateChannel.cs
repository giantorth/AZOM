namespace MozaPlugin.UI.UpdateCheck
{
    /// <summary>
    /// Release stream the in-plugin update checker should follow.
    /// Persisted as int in <see cref="MozaPluginSettings.UpdateChannel"/>.
    /// </summary>
    public enum UpdateChannel
    {
        Stable = 0,
        Dev = 1,
    }
}
