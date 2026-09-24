using System.Drawing;
using System.Windows.Forms;
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
        _icon = new NotifyIcon { Text = "SSH Manager", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => _host.ShowMainWindow();
        _icon.BalloonTipClicked += (_, _) => _host.ShowMainWindow();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !_host.Vault.IsUnlocked) _host.ShowMainWindow();
        };
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        UpdateState();
    }

    public void UpdateState()
    {
        var unlocked = _host.Vault.IsUnlocked;
        var old = _current;
        _current = IconFactory.Create(unlocked);
        _icon.Icon = _current;
        old?.Dispose();
        var agent = _host.Agent.PipeName ?? "не запущен";
        var text = $"SSH Manager — {(unlocked ? "разблокирован" : "заблокирован")}\nАгент: {agent}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Balloon(string title, string text, ToolTipIcon kind = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(4000, title, text, kind);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Открыть SSH Manager", null, (_, _) => _host.ShowMainWindow())
        {
            Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold),
        };
        _menu.Items.Add(open);

        var connect = new ToolStripMenuItem("Подключиться");
        if (_host.Vault.IsUnlocked)
        {
            var servers = _host.Vault.Data.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var group in servers.GroupBy(s => s.Group ?? "").OrderBy(g => g.Key))
            {
                var target = connect.DropDownItems;
                if (!string.IsNullOrWhiteSpace(group.Key))
                {
                    var sub = new ToolStripMenuItem(group.Key);
                    connect.DropDownItems.Add(sub);
                    target = sub.DropDownItems;
                }
                foreach (var s in group) target.Add(ServerItem(s));
            }
            if (servers.Count == 0) connect.DropDownItems.Add(new ToolStripMenuItem("(нет серверов)") { Enabled = false });
        }
        else
        {
            connect.DropDownItems.Add(new ToolStripMenuItem("Разблокировать…", null, (_, _) => _host.ShowMainWindow()));
        }
        _menu.Items.Add(connect);
        _menu.Items.Add(new ToolStripSeparator());

        if (_host.Vault.IsUnlocked)
            _menu.Items.Add(new ToolStripMenuItem("Заблокировать", null, (_, _) => _host.Lock()));
        else
            _menu.Items.Add(new ToolStripMenuItem("Разблокировать…", null, (_, _) => _host.ShowMainWindow()));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => _host.Exit()));
    }

    private ToolStripMenuItem ServerItem(ServerEntry s)
    {
        var item = new ToolStripMenuItem($"{s.Name}    {s.Display}", null, (_, _) => _host.Connect(s.Id));
        item.ToolTipText = s.Auth == AuthMode.Key ? "Вход по ключу" : "Вход по паролю";
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
