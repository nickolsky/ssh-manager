using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Scripts;

/// <summary>
/// Stores a finished script run on its server: the run history, the results as server attributes and the ports to
/// monitor. Shared by the run window and AI agents (MCP).
/// </summary>
public static class ScriptRunRecorder
{
    public const int KeepRuns = 30;

    /// <summary>Only the parameters worth keeping in the history: visible ones, never secrets.</summary>
    public static Dictionary<string, string> HistoryParams(ScriptManifest manifest, IReadOnlyDictionary<string, string> values) =>
        manifest.Params.Where(p => p.IsVisible(values) && p.Type != ScriptParamType.Secret && values.ContainsKey(p.Name))
            .ToDictionary(p => p.Name, p => values[p.Name].Trim());

    /// <summary>
    /// Completes <paramref name="run"/> (value="…${PARAM}…" results of a successful run) and saves it.
    /// Returns true when ports were put on monitoring, so the caller can check the server now.
    /// </summary>
    public static bool Record(VaultService vault, ServerEntry server, ScriptEntry script, ScriptManifest manifest, ScriptRun run,
        IReadOnlyDictionary<string, string> values)
    {
        if (run.ExitCode == 0)
        {
            var vars = new Dictionary<string, string>(values) { ["SSHM_HOST"] = server.Host, ["SSHM_SERVER_NAME"] = server.Name };
            foreach (var (key, value) in manifest.TemplateResults(vars)) run.Results.TryAdd(key, value);
        }
        var addedPorts = false;
        vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
            if (s == null) return;
            s.ScriptRuns.Add(run);
            if (s.ScriptRuns.Count > KeepRuns) s.ScriptRuns.RemoveRange(0, s.ScriptRuns.Count - KeepRuns);
            foreach (var (key, value) in run.Results)
            {
                var def = manifest.Result(key);
                var attr = s.Attributes.FirstOrDefault(a => a.Key == key);
                if (attr == null) s.Attributes.Add(attr = new ServerAttribute { Key = key });
                attr.Label = def?.Label ?? key;
                attr.Value = value;
                attr.Source = script.Name;
                attr.Updated = DateTime.Now;
                if (def?.MonitorName is { } monitor && int.TryParse(value, out var port) && port is > 0 and < 65536 &&
                    s.MonitoredPorts.All(p => p.Port != port))
                {
                    s.MonitoredPorts.Add(new MonitoredPort { Port = port, Name = monitor });
                    addedPorts = true;
                }
            }
            if (run.ExitCode == 0)
                s.Attributes.RemoveAll(a => a.Source == script.Name && manifest.IsStale(a.Key, run.Results));
        });
        return addedPorts;
    }
}
