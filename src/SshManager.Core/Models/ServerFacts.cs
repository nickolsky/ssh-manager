namespace SshManager.Core.Models;

/// <summary>Collected information about a server; stored in the vault next to the server entry.</summary>
public sealed class ServerFacts
{
    /// <summary>os-release ID, e.g. "ubuntu".</summary>
    public string? OsId { get; set; }
    /// <summary>os-release ID_LIKE, e.g. "debian".</summary>
    public string? OsLike { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsPrettyName { get; set; }
    public string? Kernel { get; set; }
    public DateTime? InventoryUpdated { get; set; }
    public string? InventoryError { get; set; }
    public bool DockerAvailable { get; set; }
    public List<ContainerInfo> Containers { get; set; } = [];
    public List<ServiceInfo> Services { get; set; } = [];
    public List<PortForward> Forwards { get; set; } = [];
    public GeoInfo? Geo { get; set; }

    /// <summary>"Ubuntu 24.04" style label.</summary>
    public string? OsLabel =>
        OsName == null ? OsPrettyName : string.IsNullOrWhiteSpace(OsVersion) ? OsName : $"{OsName} {OsVersion}";
}

public sealed class GeoInfo
{
    public string Ip { get; set; } = "";
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public string? Isp { get; set; }
    public string? Language { get; set; }
    public DateTime Updated { get; set; }

    public string Label => string.Join(", ", new[] { Country, Region, City }
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
}

public sealed class ContainerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    /// <summary>running, exited, restarting…</summary>
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public string Ports { get; set; } = "";

    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed class ServiceInfo
{
    /// <summary>systemd unit without ".service".</summary>
    public string Unit { get; set; } = "";
    /// <summary>Friendly product name from the well-known list.</summary>
    public string Title { get; set; } = "";
    /// <summary>active / inactive / failed.</summary>
    public string Active { get; set; } = "";
    /// <summary>running / exited / dead…</summary>
    public string Sub { get; set; } = "";

    public bool IsRunning => Active == "active";
}

/// <summary>A DNAT rule: traffic to ListenPort on this server goes to TargetIp:TargetPort.</summary>
public sealed class PortForward
{
    public const string CommentPrefix = "sshm:";

    public string Protocol { get; set; } = "tcp";
    /// <summary>Port or range ("1000:2000").</summary>
    public string ListenPort { get; set; } = "";
    public string TargetIp { get; set; } = "";
    public string TargetPort { get; set; } = "";
    public string? InInterface { get; set; }
    public string? Comment { get; set; }
    /// <summary>The rule as printed by iptables -S (used to delete forwards created elsewhere).</summary>
    public string? Rule { get; set; }

    /// <summary>Created by SSH Manager (tagged with an "sshm:&lt;id&gt;" comment).</summary>
    public bool Managed => Comment?.StartsWith(CommentPrefix, StringComparison.Ordinal) == true;
    public string? ManagedId => Managed ? Comment![CommentPrefix.Length..] : null;

    public string EffectiveTargetPort => string.IsNullOrEmpty(TargetPort) ? ListenPort : TargetPort;
    public string Key => $"{Protocol}/{ListenPort}>{TargetIp}:{EffectiveTargetPort}";
}
