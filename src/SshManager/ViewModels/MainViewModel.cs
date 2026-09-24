using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;
using SshManager.Mvvm;
using SshManager.Services;
using SshManager.Views;

namespace SshManager.ViewModels;

public sealed class ServerRow(ServerEntry entry, string authText)
{
    public ServerEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string GroupName => string.IsNullOrWhiteSpace(Entry.Group) ? "Без группы" : Entry.Group;
    public string Address => Entry.Display;
    public string AuthText { get; } = authText;
    public string LastConnected => Entry.LastConnected?.ToString("dd.MM.yyyy HH:mm") ?? "—";
    public string Notes => Entry.Notes ?? "";
}

public sealed class KeyRow(KeyEntry entry, string usedBy)
{
    public KeyEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Type => Entry.Type switch { "ssh-ed25519" => "Ed25519", "ssh-rsa" => "RSA", var t => t };
    public string Fingerprint => Entry.Fingerprint;
    public string Comment => Entry.Comment;
    public string UsedBy { get; } = usedBy;
    public string Created => Entry.Created.ToString("dd.MM.yyyy");
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private ServerRow? _selectedServer;
    private KeyRow? _selectedKey;
    private string _search = "";
    private string _status = "";
    private bool _busy;

    public MainViewModel(AppHost host)
    {
        _host = host;
        ServersView = CollectionViewSource.GetDefaultView(Servers);
        ServersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerRow.GroupName)));
        ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRow.GroupName), ListSortDirection.Ascending));
        ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerRow.Name), ListSortDirection.Ascending));
        ServersView.Filter = FilterServer;

        bool HasServer() => SelectedServer != null && !Busy;
        bool HasKey() => SelectedKey != null;

        ConnectCommand = new RelayCommand(Connect, HasServer);
        AddServerCommand = new RelayCommand(AddServer);
        EditServerCommand = new RelayCommand(EditServer, HasServer);
        DuplicateServerCommand = new RelayCommand(DuplicateServer, HasServer);
        DeleteServerCommand = new RelayCommand(DeleteServer, HasServer);
        CopyCommandCommand = new RelayCommand(CopyCommand, HasServer);
        SetupKeyCommand = new RelayCommand(SetupKey, () => HasServer() && !string.IsNullOrEmpty(SelectedServer!.Entry.Password));
        TestCommand = new RelayCommand(Test, HasServer);

        GenerateKeyCommand = new RelayCommand(GenerateKey);
        ImportKeyCommand = new RelayCommand(ImportKey);
        CopyPublicKeyCommand = new RelayCommand(CopyPublicKey, HasKey);
        ExportPrivateKeyCommand = new RelayCommand(ExportPrivateKey, HasKey);
        RenameKeyCommand = new RelayCommand(RenameKey, HasKey);
        DeleteKeyCommand = new RelayCommand(DeleteKey, HasKey);

        LockCommand = new RelayCommand(() => _host.Lock());
        ChangePasswordCommand = new RelayCommand(() => new ChangePasswordWindow(_host.Vault) { Owner = Owner }.ShowDialog());
        OpenDataFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }));

        Settings = new SettingsViewModel(host);
        _host.Vault.DataChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Reload);
        Reload();
    }

    public Window? Owner { get; set; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<ServerRow> Servers { get; } = [];
    public ICollectionView ServersView { get; }
    public ObservableCollection<KeyRow> Keys { get; } = [];

    public ServerRow? SelectedServer
    {
        get => _selectedServer;
        set => Set(ref _selectedServer, value);
    }

    public KeyRow? SelectedKey
    {
        get => _selectedKey;
        set => Set(ref _selectedKey, value);
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ServersView.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            Set(ref _busy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string AgentStatus => _host.Agent.PipeName switch
    {
        null => "Агент не запущен (pipe занят другим приложением)",
        var p when _host.Agent.UsesDefaultPipe => $"Агент: \\\\.\\pipe\\{p} — доступен для ssh, git, VS Code",
        var p => $"Агент: \\\\.\\pipe\\{p} (стандартный pipe занят; для внешних клиентов задайте SSH_AUTH_SOCK)",
    };

    public string Summary => $"Серверов: {Servers.Count}   Ключей: {Keys.Count}";

    public ICommand ConnectCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand EditServerCommand { get; }
    public ICommand DuplicateServerCommand { get; }
    public ICommand DeleteServerCommand { get; }
    public ICommand CopyCommandCommand { get; }
    public ICommand SetupKeyCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand GenerateKeyCommand { get; }
    public ICommand ImportKeyCommand { get; }
    public ICommand CopyPublicKeyCommand { get; }
    public ICommand ExportPrivateKeyCommand { get; }
    public ICommand RenameKeyCommand { get; }
    public ICommand DeleteKeyCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public void Reload()
    {
        if (!_host.Vault.IsUnlocked) return;
        var data = _host.Vault.Data;
        var selectedServer = SelectedServer?.Entry.Id;
        var selectedKey = SelectedKey?.Entry.Id;
        var keysById = data.Keys.ToDictionary(k => k.Id);

        Servers.Clear();
        foreach (var s in data.Servers)
        {
            var auth = s.Auth == AuthMode.Key
                ? "🔑 " + (s.KeyId is { } id && keysById.TryGetValue(id, out var k) ? k.Name : "ключ не найден")
                : "Пароль";
            Servers.Add(new ServerRow(s, auth));
        }

        Keys.Clear();
        foreach (var k in data.Keys.OrderBy(k => k.Name))
        {
            var used = data.Servers.Where(s => s.KeyId == k.Id && s.Auth == AuthMode.Key).Select(s => s.Name).ToList();
            Keys.Add(new KeyRow(k, used.Count == 0 ? "—" : string.Join(", ", used)));
        }

        SelectedServer = Servers.FirstOrDefault(r => r.Entry.Id == selectedServer);
        SelectedKey = Keys.FirstOrDefault(r => r.Entry.Id == selectedKey);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AgentStatus));
    }

    private bool FilterServer(object o)
    {
        if (string.IsNullOrWhiteSpace(_search)) return true;
        var r = (ServerRow)o;
        var q = _search.Trim();
        return r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               r.Entry.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               r.Entry.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               r.GroupName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               r.Notes.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    // ---------- servers ----------

    public void Connect()
    {
        if (SelectedServer == null) return;
        try
        {
            _host.Launcher.Launch(SelectedServer.Entry);
            Status = $"Открыта сессия: {SelectedServer.Name}";
        }
        catch (Exception ex)
        {
            Warn("Не удалось открыть сессию", ex.Message);
        }
    }

    private void AddServer()
    {
        var entry = new ServerEntry { Group = SelectedServer?.Entry.Group ?? "" };
        if (new ServerEditorWindow(_host, entry, isNew: true) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d => d.Servers.Add(entry));
        SelectedServer = Servers.FirstOrDefault(r => r.Entry.Id == entry.Id);
    }

    private void EditServer()
    {
        if (SelectedServer == null) return;
        var copy = SelectedServer.Entry.Clone();
        if (new ServerEditorWindow(_host, copy, isNew: false) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d =>
        {
            var i = d.Servers.FindIndex(s => s.Id == copy.Id);
            if (i >= 0) d.Servers[i] = copy;
        });
    }

    private void DuplicateServer()
    {
        if (SelectedServer == null) return;
        var copy = SelectedServer.Entry.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (копия)";
        copy.LastConnected = null;
        if (new ServerEditorWindow(_host, copy, isNew: true) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d => d.Servers.Add(copy));
    }

    private void DeleteServer()
    {
        if (SelectedServer == null) return;
        var s = SelectedServer.Entry;
        if (!Confirm($"Удалить сервер «{s.Name}» ({s.Display})?\nКлючи останутся в хранилище.")) return;
        _host.Vault.Update(d => d.Servers.RemoveAll(x => x.Id == s.Id));
    }

    private void CopyCommand()
    {
        if (SelectedServer == null) return;
        Clipboard.SetText(_host.Launcher.CommandLine(SelectedServer.Entry));
        Status = "Команда ssh скопирована в буфер обмена";
    }

    private void SetupKey()
    {
        if (SelectedServer == null) return;
        new KeySetupWindow(_host, SelectedServer.Entry.Clone()) { Owner = Owner }.ShowDialog();
    }

    private async void Test()
    {
        if (SelectedServer == null) return;
        var s = SelectedServer.Entry.Clone();
        Busy = true;
        Status = $"Проверка подключения к {s.Name}…";
        try
        {
            var result = await Task.Run(() => _host.KeySetup.Test(s));
            Status = $"{s.Name}: подключение успешно";
            MessageBox.Show(Owner!, $"Подключение к {s.Display} успешно.\n\n{result}", "Проверка подключения",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = $"{s.Name}: ошибка подключения";
            Warn("Проверка подключения", $"Не удалось подключиться к {s.Display}:\n\n{ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------- keys ----------

    private void GenerateKey()
    {
        var dlg = new KeyGenerateWindow { Owner = Owner };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var key = dlg.Result;
        _host.Vault.Update(d => d.Keys.Add(key));
        KeyService.EnsurePublicKeyFile(key);
        SelectedKey = Keys.FirstOrDefault(k => k.Entry.Id == key.Id);
        Status = $"Ключ «{key.Name}» создан";
    }

    private void ImportKey()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Импорт приватного ключа",
            Filter = "Все файлы|*.*|PuTTY (*.ppk)|*.ppk|PEM (*.pem;*.key)|*.pem;*.key",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (ofd.ShowDialog(Owner) != true) return;
        string text;
        try
        {
            text = File.ReadAllText(ofd.FileName);
        }
        catch (Exception ex)
        {
            Warn("Импорт ключа", ex.Message);
            return;
        }
        var name = Path.GetFileNameWithoutExtension(ofd.FileName);
        string? passphrase = null;
        while (true)
        {
            try
            {
                var key = KeyService.Import(text, name, passphrase);
                if (_host.Vault.Data.Keys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    Warn("Импорт ключа", "Этот ключ уже есть в хранилище.");
                    return;
                }
                _host.Vault.Update(d => d.Keys.Add(key));
                KeyService.EnsurePublicKeyFile(key);
                Status = $"Ключ «{name}» импортирован ({key.Fingerprint})";
                return;
            }
            catch (EncryptedKeyException)
            {
                passphrase = InputDialog.Ask(Owner, "Импорт ключа",
                    passphrase == null ? "Ключ защищён паролем. Введите passphrase:" : "Неверная passphrase. Попробуйте ещё раз:",
                    password: true);
                if (passphrase == null) return;
            }
            catch (Exception ex)
            {
                if (passphrase != null && ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
                {
                    passphrase = InputDialog.Ask(Owner, "Импорт ключа", "Неверная passphrase. Попробуйте ещё раз:", password: true);
                    if (passphrase == null) return;
                    continue;
                }
                Warn("Импорт ключа", "Не удалось прочитать ключ: " + ex.Message);
                return;
            }
        }
    }

    private void CopyPublicKey()
    {
        if (SelectedKey == null) return;
        Clipboard.SetText(SelectedKey.Entry.PublicKey);
        Status = "Публичный ключ скопирован в буфер обмена";
    }

    private void ExportPrivateKey()
    {
        if (SelectedKey == null) return;
        if (!Confirm("Приватный ключ будет сохранён в файл БЕЗ шифрования.\nХраните этот файл в надёжном месте. Продолжить?"))
            return;
        var sfd = new SaveFileDialog
        {
            Title = "Экспорт приватного ключа",
            FileName = "id_" + KeyService.SanitizeComment(SelectedKey.Name.Replace('@', '_')),
            Filter = "OpenSSH private key|*.*",
        };
        if (sfd.ShowDialog(Owner) != true) return;
        FileAcl.WritePrivate(sfd.FileName, SelectedKey.Entry.PrivateKey);
        File.WriteAllText(sfd.FileName + ".pub", SelectedKey.Entry.PublicKey + "\n");
        Status = $"Ключ сохранён: {sfd.FileName}";
    }

    private void RenameKey()
    {
        if (SelectedKey == null) return;
        var id = SelectedKey.Entry.Id;
        var name = InputDialog.Ask(Owner, "Переименовать ключ", "Новое название:", SelectedKey.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _host.Vault.Update(d => d.Keys.First(k => k.Id == id).Name = name.Trim());
    }

    private void DeleteKey()
    {
        if (SelectedKey == null) return;
        var key = SelectedKey.Entry;
        var users = _host.Vault.Data.Servers.Where(s => s.KeyId == key.Id && s.Auth == AuthMode.Key).Select(s => s.Name).ToList();
        if (users.Count > 0)
        {
            Warn("Удаление ключа", "Ключ используется серверами:\n" + string.Join("\n", users) +
                                   "\n\nСначала переключите их на другой ключ или пароль.");
            return;
        }
        if (!Confirm($"Удалить ключ «{key.Name}»?\n{key.Fingerprint}\n\nЕсли у вас нет копии, восстановить его будет невозможно.")) return;
        _host.Vault.Update(d =>
        {
            d.Keys.RemoveAll(k => k.Id == key.Id);
            foreach (var s in d.Servers.Where(s => s.KeyId == key.Id)) s.KeyId = null;
        });
        var pub = Path.Combine(AppPaths.PublicKeyDir, key.Id.ToString("N") + ".pub");
        if (File.Exists(pub)) File.Delete(pub);
    }

    // ---------- helpers ----------

    private bool Confirm(string text) =>
        MessageBox.Show(Owner!, text, "SSH Manager", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) ==
        MessageBoxResult.Yes;

    private void Warn(string title, string text) =>
        MessageBox.Show(Owner!, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
