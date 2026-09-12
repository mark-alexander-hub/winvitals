using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Network adapters, their drivers, and what this machine is listening for.
///
/// The listener list is read through .NET's own IP statistics rather than by
/// parsing netstat, so it does not depend on the console output language.
/// </summary>
public sealed class NetworkCollector : ICollector
{
    public string Id => "network";
    public string Name => "Network";
    public string Blurb => "Network adapters, their driver versions, and which ports this machine listens on.";

    /// <summary>Ports worth a comment when they are open to every network interface.</summary>
    private static readonly Dictionary<int, (string Service, Severity Level, string Note)> Notable = new()
    {
        [23] = ("Telnet", Severity.Critical,
            "Telnet sends everything, passwords included, as readable text. There is no safe way to expose it."),
        [3389] = ("Remote Desktop", Severity.Advisory,
            "Remote Desktop is reachable from the network. Fine if you use it deliberately."),
        [5900] = ("VNC", Severity.Warning,
            "VNC is listening. Many VNC servers default to weak or no authentication."),
        [5985] = ("WinRM (HTTP)", Severity.Advisory,
            "Windows Remote Management is listening unencrypted. Normal on managed corporate machines."),
        [22] = ("SSH", Severity.Advisory,
            "An SSH server is running. Expected if you installed one on purpose."),
        [445] = ("SMB file sharing", Severity.Advisory,
            "Windows file sharing is listening. Standard on Windows, but it is the service worth keeping "
            + "behind a firewall on public networks."),
    };

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        Adapters(ctx, m);
        Listeners(m);
    }

    private static void Adapters(ScanContext ctx, ModuleResult m)
    {
        // Not ToDictionary: device names are not unique. Windows creates several
        // adapters called "Microsoft Wi-Fi Direct Virtual Adapter", and a duplicate key
        // would throw and take the whole network module down with it.
        var drivers = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in Wmi.Query(
                     "SELECT DeviceName, DriverVersion, DriverDate, DriverProviderName "
                     + "FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'"))
        {
            var name = driver.Str("DeviceName");
            if (name.Length > 0) drivers.TryAdd(name, driver);
        }

        var up = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            if (isUp) up++;

            // Only detail the adapters that are actually doing something; a laptop has a
            // long tail of disconnected virtual adapters nobody needs to read about.
            if (!isUp) continue;

            var speed = nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mb/s" : "unknown speed";
            var detail = $"{nic.NetworkInterfaceType}, {speed}";

            if (drivers.TryGetValue(nic.Description, out var driver))
            {
                var date = driver.Date("DriverDate");
                detail += $" — driver {driver.Str("DriverVersion")}"
                          + (date.HasValue ? $" ({date:yyyy-MM-dd})" : "");
            }

            m.Fact(nic.Name, detail, nic.Description);

            try
            {
                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString());
                var dns = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString());

                if (addresses.Any()) m.Fact($"  {nic.Name} address", string.Join(", ", addresses));
                if (dns.Any()) m.Fact($"  {nic.Name} DNS", string.Join(", ", dns));
            }
            catch
            {
                // An adapter can disappear between enumeration and query.
            }
        }

        if (up == 0)
        {
            m.Add(Severity.Advisory, "network.offline", "No network adapter is connected",
                what: "Nothing is currently online.",
                why: "Expected if you are deliberately offline. Otherwise the adapter may be disabled or its "
                     + "driver may have failed — check the devices section.",
                action: "If you expected to be connected, check Wi-Fi is on and look for device errors above.");
        }
        else
        {
            m.Ok("network.adapters", $"{up} network adapter(s) connected",
                "At least one interface is up and configured.");
        }

        // Driver versions are reported, never judged. See the firmware note in the
        // system module for why WinVitals refuses to guess what "current" means.
        var netDrivers = drivers.Values
            .Select(d => (Name: d.Str("DeviceName"), Version: d.Str("DriverVersion"), Date: d.Date("DriverDate")))
            .Where(d => d.Name.Length > 0)
            .OrderBy(d => d.Name)
            .ToList();

        if (netDrivers.Count > 0)
        {
            m.Add(Severity.Ok, "network.drivers", "Network driver versions",
                what: $"{netDrivers.Count} network driver(s) are installed. Versions and dates are listed in "
                      + "the evidence below.",
                why: "WinVitals reports these without judging them. A driver from 2022 is not automatically out "
                     + "of date: for plenty of hardware it is the final release the vendor ever shipped, and "
                     + "'updating' it means installing something older or generic.",
                action: "If you are troubleshooting this adapter, compare these against the vendor's page for "
                        + "your exact model before assuming an update exists.",
                evidence: string.Join("\n", netDrivers.Select(d =>
                    $"{(d.Date.HasValue ? d.Date.Value.ToString("yyyy-MM-dd") : "          ")}  "
                    + $"{d.Version,-20} {d.Name}")));
        }
    }

    private static void Listeners(ModuleResult m)
    {
        IPEndPoint[] tcp;
        try
        {
            tcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (Exception ex)
        {
            m.Add(Severity.Unknown, "network.listeners", "Could not read listening ports", ex.Message);
            return;
        }

        // Bound to a specific address means local-only in practice; bound to Any means
        // anything that can route to this machine can try to connect.
        var exposed = tcp
            .Where(e => e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))
            .Select(e => e.Port)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        m.Fact("Ports open to the network", exposed.Count.ToString());

        var evidence = string.Join("\n", exposed.Select(p =>
            Notable.TryGetValue(p, out var info) ? $"{p,6}  {info.Service}" : $"{p,6}"));

        var flagged = exposed.Where(Notable.ContainsKey).ToList();
        if (flagged.Count == 0)
        {
            m.Ok("network.listeners", $"{exposed.Count} port(s) listening, none of them notable",
                "Nothing is listening on a port associated with remote access.", evidence);
            return;
        }

        foreach (var port in flagged)
        {
            var (service, level, note) = Notable[port];
            m.Add(level, $"network.port.{port}", $"{service} is listening on port {port}",
                what: $"This machine accepts connections on TCP {port} from any network it joins.",
                why: note + " Whether that matters depends entirely on the networks this laptop connects to: "
                     + "a service that is harmless at home is exposed in a hotel or cafe.",
                action: level == Severity.Critical
                    ? "Turn this service off. There is no configuration that makes it safe to expose."
                    : "Leave it if you use it deliberately. If you do not recognise it, find out what is "
                      + "listening before deciding.",
                command: $"netstat -ano | findstr :{port}",
                evidence: evidence);
        }
    }
}
