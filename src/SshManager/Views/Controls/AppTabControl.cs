using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SshManager.Views.Controls;

/// <summary>
/// TabControl whose Home / End only switch tabs while a tab header has the focus. WebView2 hands keys it has not
/// consumed yet (Home, End…) to WPF first, so the stock control took them from the terminal and the editor.
/// </summary>
public sealed class AppTabControl : TabControl
{
    // look exactly like a TabControl: the Fluent theme styles are keyed by that type, not by this subclass
    static AppTabControl() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AppTabControl), new FrameworkPropertyMetadata(typeof(TabControl)));

    public AppTabControl() => SetResourceReference(StyleProperty, typeof(TabControl));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Home or Key.End && e.OriginalSource is not TabItem) return;
        base.OnKeyDown(e);
    }
}
