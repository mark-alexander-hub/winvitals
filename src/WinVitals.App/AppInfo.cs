using WinVitals.Core;
using WinVitals.Collectors;

namespace WinVitals.App;

public static class AppInfo
{
    public const string Version = "0.4.0";

    /// <summary>Where releases live; used by the update check and the README.</summary>
    public const string Repository = "mark-alexander-hub/winvitals";

    /// <summary>Order here is the order of sections in the report and the scan.</summary>
    public static readonly ICollector[] AllCollectors =
    {
        new SystemCollector(),
        new BootCollector(),
        new PowerCollector(),
        new StorageCollector(),
        new BatteryCollector(),
        new MemoryCollector(),
        new DeviceCollector(),
        new StartupCollector(),
        new NetworkCollector(),
        new SecurityCollector(),
        new UpdateCollector(),
        new ReliabilityCollector(),
    };

    /// <summary>Resolves a playbook's module list into collectors, preserving scan order.</summary>
    public static List<ICollector> CollectorsFor(Playbook playbook)
    {
        if (playbook.Modules.Count == 0) return AllCollectors.ToList();

        return AllCollectors
            .Where(c => playbook.Modules.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
