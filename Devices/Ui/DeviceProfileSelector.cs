using System.Windows;
using System.Windows.Controls;

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
            // ProfileList's root wrapper carries a 30,15,30,0 margin sized for SimHub's
            // own pages; drop it so the list sits flush in the tab strip.
            if (list is ContentControl cc && cc.Content is FrameworkElement inner
                && inner.Margin != default)
                inner.Margin = default;

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
