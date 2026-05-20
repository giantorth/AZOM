using System.Runtime.InteropServices;
using System.Windows;

[assembly: ComVisible(false)]
[assembly: Guid("b3c4d5e6-f7a8-9012-bcde-f12345678901")]

// Custom WPF controls in UI/Controls/ resolve their default styles from
// Themes/Generic.xaml. The SourceAssembly setting tells WPF to look in
// this DLL — no separate theme assemblies are shipped.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
