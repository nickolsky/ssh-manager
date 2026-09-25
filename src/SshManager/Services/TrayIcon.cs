using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SshManager.Core;
using SshManager.Core.Models;

namespace SshManager.Services;

/// <summary>System tray icon (WinForms NotifyIcon) with quick-connect menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private Icon? _current;

    public TrayIcon(AppHost host)
    {
        _host = host;
        // the menu is shown by hand (not ContextMenuStrip =): built first, then placed at the mouse, so its size and screen are right
        _icon = new NotifyIcon { Text = AppPaths.ProductTitle, Visible = true };
        _icon.DoubleClick += (_, _) => _host.ShowMainWindow();
        _icon.BalloonTipClicked += (_, _) => _host.ShowMainWindow();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !_host.Vault.IsUnlocked) _host.ShowMainWindow();
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
        };
        BuildMenu();
        UpdateState();
    }

    /// <summary>
    /// Opens the menu at a screen point (the mouse): above and to the left of it, like the shell's own tray menus,
    /// kept inside the working area of the monitor the point is on.
    /// </summary>
    internal void ShowMenu(Point at)
    {
        BuildMenu();
        // the menu must own the foreground, or it does not close when clicking elsewhere
        SetForegroundWindow(new HandleRef(_menu, _menu.Handle));
        var size = _menu.GetPreferredSize(Size.Empty);
        var area = Screen.FromPoint(at).WorkingArea;
        var x = Math.Clamp(at.X - size.Width, area.Left, Math.Max(area.Left, area.Right - size.Width));
        var y = Math.Clamp(at.Y - size.Height, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        _menu.Show(new Point(x, y));
    }

    internal ContextMenuStrip Menu => _menu;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(HandleRef hWnd);

    public void UpdateState()
    {
        var unlocked = _host.Vault.IsUnlocked;
        var old = _current;
        _current = IconFactory.Create(unlocked, Math.Max(16, SystemInformation.SmallIconSize.Width));
        _icon.Icon = _current;
        old?.Dispose();
        var agent = _host.Agent.PipeName ?? L.Get("Tray.AgentNotRunning");
        var text = $"{AppPaths.ProductTitle} — {L.Get(unlocked ? "Tray.Unlocked" : "Tray.Locked")}\n{L.Get("Tray.Agent")} {agent}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Balloon(string title, string text, ToolTipIcon kind = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(4000, title, text, kind);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem(L.Get("Tray.Open"), null, (_, _) => _host.ShowMainWindow())
        {
            Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold),
        };
        _menu.Items.Add(open);

        var connect = new ToolStripMenuItem(L.Get("Tray.Connect"));
        if (_host.Vault.IsUnlocked)
        {
            var servers = _host.Vault.Data.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var group in servers.GroupBy(s => s.Group ?? "").OrderBy(g => g.Key))
            {
                var target = connect.DropDownItems;
                if (!string.IsNullOrWhiteSpace(group.Key))
                {
                    // nested groups "a/b" become nested submenus
                    foreach (var part in group.Key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var sub = target.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Name == "group" && i.Tag as string == part);
                        if (sub == null)
                        {
                            sub = new ToolStripMenuItem(part) { Tag = part, Name = "group" };
                            target.Add(sub);
                        }
                        target = sub.DropDownItems;
                    }
                }
                foreach (var s in group) target.Add(ServerItem(s));
            }
            if (servers.Count == 0) connect.DropDownItems.Add(new ToolStripMenuItem(L.Get("Tray.NoServers")) { Enabled = false });
        }
        else
        {
            connect.DropDownItems.Add(new ToolStripMenuItem(L.Get("Tray.Unlock"), null, (_, _) => _host.ShowMainWindow()));
        }
        _menu.Items.Add(connect);
        _menu.Items.Add(new ToolStripSeparator());

        var language = new ToolStripMenuItem(L.Get("Tray.Language"));
        var configured = _host.SettingsStore.Settings.Language;
        language.DropDownItems.Add(new ToolStripMenuItem(L.Get("Lang.System"), null, (_, _) => _host.SetLanguage(null)) { Checked = configured == null });
        language.DropDownItems.Add(new ToolStripMenuItem("Русский", null, (_, _) => _host.SetLanguage(L.Russian)) { Checked = configured == L.Russian });
        language.DropDownItems.Add(new ToolStripMenuItem("English", null, (_, _) => _host.SetLanguage(L.English)) { Checked = configured == L.English });
        _menu.Items.Add(language);
        _menu.Items.Add(new ToolStripSeparator());

        if (_host.Vault.IsUnlocked)
            _menu.Items.Add(new ToolStripMenuItem(L.Get("Tray.Lock"), null, (_, _) => _host.Lock()));
        else
            _menu.Items.Add(new ToolStripMenuItem(L.Get("Tray.Unlock"), null, (_, _) => _host.ShowMainWindow()));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L.Get("Tray.Exit"), null, (_, _) => _host.Exit()));
    }

    private ToolStripMenuItem ServerItem(ServerEntry s)
    {
        var item = new ToolStripMenuItem($"{s.Name}    {s.Display}", null, (_, _) => _host.Connect(s.Id));
        item.ToolTipText = L.Get(s.Auth == AuthMode.Key ? "Tray.ByKey" : "Tray.ByPassword");
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _current?.Dispose();
    }
}
