using WinVitals.Core;
using WinVitals.Collectors;

namespace WinVitals.App;

public static class AppInfo
{
    public const string Version = "0.3.0";

    /// <summary>Order here is the order of sections in the report and the scan.</summary>
    public static readonly ICollector[] AllCollectors =
    {
        new SystemCollector(),
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
