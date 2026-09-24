using System.Text.Json;
using Microsoft.Win32;
using SshManager.Core.Models;

namespace SshManager.Core.Storage;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new(VaultService.Json) { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();
    public bool IsFirstRun { get; private set; }

    public void Load()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            IsFirstRun = true;
            Settings = new AppSettings();
            return;
        }
        try
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options)
                       ?? new AppSettings();
        }
        catch (JsonException)
        {
            Settings = new AppSettings();
        }
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Settings, Options));
}

/// <summary>Per-user autostart via HKCU\...\Run (no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SshManager";

    public static string Command => $"\"{Environment.ProcessPath ?? AppPaths.MainExe}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Creates, refreshes (if the exe moved) or removes the Run entry.</summary>
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            if (key.GetValue(ValueName) as string != Command)
                key.SetValue(ValueName, Command);
        }
        else if (key.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
