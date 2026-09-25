using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SshManager.Core.Models;
using SshManager.Core.Scripts;
using SshManager.Mvvm;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>Editable value of one script parameter.</summary>
public sealed class ParamRow(ScriptParam param, string value, Action changed) : ObservableObject
{
    private string _value = value;
    private bool _isVisible = true;

    public ScriptParam Param { get; } = param;
    public string Name => Param.Name;
    public string Label => Param.Label + (Param.Required ? " *" : "");
    public string? Hint => Param.Hint;
    public string Kind => Param.Type.ToString();
    public IReadOnlyList<string> Options => Param.Options;

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? "")) return;
            OnPropertyChanged(nameof(Checked));
            changed();
        }
    }

    public bool Checked
    {
        get => _value == ScriptParam.True;
        set => Value = value ? ScriptParam.True : ScriptParam.False;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }
}

public sealed record ResultRow(string Label, string Value)
{
    /// <summary>The QR button: links (VPN, sites, Amnezia keys) only.</summary>
    public Visibility QrVisibility => QrPayloads.CanShow(Value) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Runs an install script on one server inside the app: parameter form, live output, and results
/// (KEY=value lines the script writes to $SSHM_RESULT) saved as server attributes.
/// </summary>
public partial class ScriptRunWindow : Window
{
    private const int MaxOutputChars = 2_000_000;

    private readonly AppHost _host;
    private readonly Guid _serverId;
    private readonly ScriptEntry _script;
    private readonly ScriptManifest _manifest;
    private readonly ObservableCollection<ParamRow> _params = [];
    private readonly StringBuilder _pendingOutput = new();
    private readonly DispatcherTimer _flush;
    private readonly DateTime _opened = DateTime.Now;
    private CancellationTokenSource? _cts;
    private DateTime _started;

    public ScriptRunWindow(AppHost host, Guid serverId, ScriptEntry script)
    {
        InitializeComponent();
        _host = host;
        _serverId = serverId;
        _script = script;
        _manifest = ScriptManifest.Parse(script.Body);
        Icon = IconFactory.CreateImage(true);

        var server = Server() ?? throw new InvalidOperationException(L.Get("Backup.ServerMissing"));
        Title = $"{script.Name} — {server.Name}";
        Header.Text = L.F("ScriptRun.Header", script.Name, server.Name, server.Display);
        if (!string.IsNullOrWhiteSpace(_manifest.Description))
        {
            Description.Text = _manifest.Description;
            Description.Visibility = Visibility.Visible;
        }
        if (script.Kind == ScriptKind.Compose)
        {
            Description.Text = (string.IsNullOrWhiteSpace(_manifest.Description) ? "" : _manifest.Description + "\n") +
                               L.F("ScriptRun.ComposeInfo", ScriptManifest.ProjectFor(script, _manifest));
            Description.Visibility = Visibility.Visible;
        }
        if (!script.Matches(server.Facts))
        {
            OsWarning.Text = L.F("ScriptRun.OsMismatch", script.OsFilter, server.Facts?.OsLabel ?? L.Get("ScriptRun.OsUnknown"));
            OsWarning.Visibility = Visibility.Visible;
        }

        var previous = server.ScriptRuns.LastOrDefault(r => r.ScriptId == script.Id)?.Params;
        foreach (var (name, value) in _manifest.InitialValues(previous))
            _params.Add(new ParamRow(_manifest.Params.First(p => p.Name == name), value, UpdateVisibility));
        UpdateVisibility();
        ParamsList.ItemsSource = _params;
        if (_params.Count == 0) ParamsBox.Visibility = Visibility.Collapsed;

        Output.Text = L.Get(_params.Count == 0 ? "ScriptRun.ReadyNoParams" : "ScriptRun.Ready");
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _flush.Tick += (_, _) => FlushOutput();
        Closing += OnClosing;
    }

    private ServerEntry? Server() =>
        _host.Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == _serverId)?.Clone(), out var s) ? s : null;

    private void UpdateVisibility()
    {
        var values = CurrentValues(all: true);
        foreach (var row in _params) row.IsVisible = row.Param.IsVisible(values);
    }

    /// <summary>Values of the parameters; hidden ones are not passed, so the script uses its own default.</summary>
    private Dictionary<string, string> CurrentValues(bool all = false) =>
        _params.Where(p => all || p.IsVisible).ToDictionary(p => p.Name, p => p.Value.Trim());

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { Tag: ParamRow row } box) row.Value = box.Password;
    }

    // ---------- run ----------

    private bool Validate(out Dictionary<string, string> values)
    {
        values = CurrentValues();
        if (_manifest.Validate(values) is not { } error) return true;
        SetStatus(error, isError: true);
        return false;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var values) || Server() is not { } server) return;
        _cts = new CancellationTokenSource();
        _started = DateTime.Now;
        SetRunning(true);
        Output.Clear();
        ResultsBox.Visibility = Visibility.Collapsed;
        SetStatus(L.Get("ScriptRun.Connecting"));
        _flush.Start();

        var run = new ScriptRun
        {
            ScriptId = _script.Id,
            ScriptName = _script.Name,
            Started = _started,
            Params = _params.Where(p => p.IsVisible && p.Param.Type != ScriptParamType.Secret).ToDictionary(p => p.Name, p => p.Value.Trim()),
        };
        ScriptRunResult? result = null;
        try
        {
            var ct = _cts.Token;
            result = await Task.Run(() => _host.Scripts.RunAsync(server, _script, values, OnOutput, ct), ct);
            run.ExitCode = result.ExitCode;
            SetStatus(result.Ok ? L.F("ScriptRun.Done", Elapsed()) : L.F("ScriptRun.Failed", result.ExitCode, Elapsed()), isError: !result.Ok);
        }
        catch (OperationCanceledException)
        {
            run.Error = L.Get("ScriptRun.Stopped");
            SetStatus(L.Get("ScriptRun.Stopped"), isError: true);
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
            OnOutput("\n" + L.Get("Common.Error") + " " + ex.Message + "\n");
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            _flush.Stop();
            FlushOutput();
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }

        run.Finished = DateTime.Now;
        run.OutputTail = Tail(Output.Text, 60);
        if (result != null) run.Results = result.Results;
        Save(server, run, values);
        ShowResults(run.Results);
        if (result?.Ok == true) _host.RefreshServer(_serverId); // containers, services, ports may have changed
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus(L.Get("ScriptRun.Stopping"));
    }

    private async void OnRunInTerminal(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var values) || Server() is not { } server) return;
        SetRunning(true);
        SetStatus(L.F("Scripts.Uploading", _script.Name, server.Name));
        try
        {
            var command = await Task.Run(() => _host.Scripts.Prepare(server, _script, values));
            _host.OpenSession(server, command, _script.Name);
            SetStatus(L.Get("ScriptRun.InTerminalStarted"));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            SetRunning(false);
        }
    }

    /// <summary>Called from the reading thread; the timer moves text to the box in batches.</summary>
    private void OnOutput(string text)
    {
        lock (_pendingOutput) _pendingOutput.Append(text);
    }

    private void FlushOutput()
    {
        string text;
        lock (_pendingOutput)
        {
            if (_pendingOutput.Length == 0)
            {
                if (_cts != null) ElapsedText.Text = Elapsed();
                return;
            }
            text = _pendingOutput.ToString();
            _pendingOutput.Clear();
        }
        if (_cts != null) SetStatus(L.Get("ScriptRun.Running"));
        var atEnd = Output.VerticalOffset + Output.ViewportHeight >= Output.ExtentHeight - 4;
        Output.AppendText(text);
        if (Output.Text.Length > MaxOutputChars) Output.Text = Output.Text[^(MaxOutputChars / 2)..];
        if (atEnd) Output.ScrollToEnd();
        ElapsedText.Text = Elapsed();
    }

    private string Elapsed()
    {
        var t = DateTime.Now - (_started == default ? _opened : _started);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string Tail(string text, int lines)
    {
        var all = text.TrimEnd().Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    // ---------- results ----------

    /// <summary>Stores the run and its results on the server (attributes, monitored ports).</summary>
    private void Save(ServerEntry server, ScriptRun run, IReadOnlyDictionary<string, string> values)
    {
        bool addedPorts;
        try
        {
            addedPorts = ScriptRunRecorder.Record(_host.Vault, server, _script, _manifest, run, values);
        }
        catch (Exception ex)
        {
            SetStatus(L.Get("ScriptRun.SaveFailed") + " " + ex.Message, isError: true);
            return;
        }
        if (addedPorts) _host.Health.CheckNow(_serverId);
    }

    private void ShowResults(Dictionary<string, string> results)
    {
        if (results.Count == 0) return;
        ResultsList.ItemsSource = results.Select(r => new ResultRow(_manifest.Result(r.Key)?.Label ?? r.Key, r.Value)).ToList();
        ResultsHeader.Text = L.F("ScriptRun.ResultsSaved", results.Count);
        ResultsBox.Visibility = Visibility.Visible;
    }

    private void OnShowQr(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ResultRow row } && QrPayloads.CanShow(row.Value))
            new QrWindow($"{Server()?.Name}: {row.Label}", row.Value) { Owner = this }.Show();
    }

    private void OnCopyResult(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value })
        {
            Clipboard.SetText(value);
            SetStatus(L.Get("ScriptRun.Copied"));
        }
    }

    // ---------- state ----------

    private void SetRunning(bool running)
    {
        Busy.Visibility = running ? Visibility.Visible : Visibility.Hidden;
        RunButton.IsEnabled = !running;
        TerminalButton.IsEnabled = !running;
        ParamsList.IsEnabled = !running;
        StopButton.Visibility = running && _cts != null ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Content = L.Get(running || _started == default ? "ScriptRun.Run" : "ScriptRun.RunAgain");
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        if (isError) StatusText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cts == null) return;
        if (MessageBox.Show(this, L.Get("ScriptRun.CloseRunning"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _cts.Cancel();
    }
}
