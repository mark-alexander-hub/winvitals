using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Battery wear: how much of the original capacity is left.
///
/// This is the number people actually want and the one Windows hides behind a
/// command-line HTML report almost nobody knows exists.
/// </summary>
public sealed class BatteryCollector : ICollector
{
    public string Id => "battery";
    public string Name => "Battery";
    public string Blurb => "How much of the battery's original capacity survives, and how it is being treated.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        var battery = Wmi.First("SELECT * FROM Win32_Battery");
        if (battery is null)
        {
            m.Ok("battery.none", "No battery detected", "This looks like a desktop, or the battery is removed.");
            return;
        }

        m.Fact("Battery", battery.Str("Name"));
        m.Fact("Chemistry", Chemistry(battery.Num("Chemistry")));

        var charge = battery.Num("EstimatedChargeRemaining");
        if (charge > 0) m.Fact("Charge now", $"{charge}%");
        m.Fact("Status", StatusText(battery.Num("BatteryStatus")));

        // The capacity numbers live in root\wmi, not in Win32_Battery. Each is a
        // separate class, and on some machines one or more is simply absent.
        var design = Wmi.First("SELECT * FROM BatteryStaticData", @"root\wmi")?.Num("DesignedCapacity") ?? 0;
        var full = Wmi.First("SELECT * FROM BatteryFullChargedCapacity", @"root\wmi")?.Num("FullChargedCapacity") ?? 0;
        var cycles = Wmi.First("SELECT * FROM BatteryCycleCount", @"root\wmi")?.Num("CycleCount") ?? 0;

        if (cycles > 0) m.Fact("Charge cycles", cycles.ToString());

        if (design <= 0 || full <= 0)
        {
            m.Add(Severity.Unknown, "battery.wear", "Battery wear could not be measured",
                "This machine does not report both the design capacity and the current full-charge capacity.",
                why: "Some firmware only exposes these to the vendor's own utility.",
                action: "Run the built-in battery report, which sometimes finds numbers WMI cannot.",
                command: "powercfg /batteryreport /output %USERPROFILE%\\Desktop\\battery.html",
                evidence: $"DesignedCapacity={design}, FullChargedCapacity={full}");
            return;
        }

        var wear = (design - full) * 100.0 / design;
        var health = 100 - wear;

        m.Fact("Design capacity", $"{design:N0} mWh");
        m.Fact("Full charge capacity", $"{full:N0} mWh", $"{health:0.#}% of original");

        var evidence = $"Designed capacity    : {design:N0} mWh\n"
                     + $"Full charge capacity : {full:N0} mWh\n"
                     + $"Health               : {health:0.#}%\n"
                     + $"Wear                 : {wear:0.#}%\n"
                     + $"Cycle count          : {(cycles > 0 ? cycles.ToString() : "not reported")}";

        const string report = "powercfg /batteryreport /output %USERPROFILE%\\Desktop\\battery.html";

        if (wear >= 50)
        {
            m.Add(Severity.Warning, "battery.wear", $"The battery has lost {wear:0}% of its original capacity",
                what: $"It now holds {full:N0} mWh of an original {design:N0} mWh — about {health:0}% of when it was new.",
                why: "At this level a full charge lasts roughly half as long as it did new, and the battery is "
                     + "likely to start shutting the machine down without warning under load.",
                action: "Plan a replacement. Until then, expect the reported time-remaining figure to be "
                        + "unreliable and keep the charger nearby.",
                command: report,
                evidence: evidence);
        }
        else if (wear >= 25)
        {
            m.Add(Severity.Advisory, "battery.wear", $"The battery has lost {wear:0}% of its original capacity",
                what: $"It holds {full:N0} mWh of an original {design:N0} mWh — about {health:0}% of when it was new.",
                why: "Normal ageing rather than a fault. Worth knowing so a shorter runtime is not mistaken for "
                     + "a software problem.",
                action: "Nothing to fix. If the vendor offers a charge limit around 80%, using it slows further wear.",
                command: report,
                evidence: evidence);
        }
        else
        {
            m.Ok("battery.wear", $"Battery health is good ({health:0}% of original capacity)",
                $"It holds {full:N0} mWh of an original {design:N0} mWh.", evidence);
        }
    }

    private static string StatusText(long code) => code switch
    {
        1 => "Discharging",
        2 => "On mains power",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critically low",
        6 => "Charging",
        7 => "Charging and high",
        8 => "Charging and low",
        9 => "Charging and critical",
        11 => "Partially charged",
        _ => "unknown",
    };

    private static string Chemistry(long code) => code switch
    {
        3 => "Lead acid",
        4 => "Nickel Cadmium",
        5 => "Nickel Metal Hydride",
        6 => "Lithium-ion",
        7 => "Zinc air",
        8 => "Lithium Polymer",
        _ => "unspecified",
    };
}
