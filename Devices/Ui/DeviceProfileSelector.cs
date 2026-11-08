using System.Windows;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// Points a device page's SimHub <c>ProfileList</c> at that device's own profile
    /// store. Called from the page's refresh tick: the store can appear later (first
    /// seen device) or change (a generic wheel page following a hot-swap, Clear
    /// settings replacing the registry), and rebinding only on a reference change
    /// keeps the tick from churning the combo.
    /// </summary>
    internal static class DeviceProfileSelector
    {
        internal static void Bind(FrameworkElement row, FrameworkElement list, object? store)
        {
            if (!ReferenceEquals(list.DataContext, store))
            {
                // ProfileList wires its inner combobox on DataContext change; clear first
                // so a same-type store swap still rebinds (as SettingsControl does).
                list.DataContext = null;
                list.DataContext = store;
            }
            var vis = store != null ? Visibility.Visible : Visibility.Collapsed;
            if (row.Visibility != vis) row.Visibility = vis;
        }
    }
}
