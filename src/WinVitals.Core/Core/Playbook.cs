namespace WinVitals.Core;

/// <summary>
/// A symptom, expressed the way someone would actually describe it, mapped to the
/// checks worth running for it.
///
/// This exists because "choose which diagnostic modules to run" is a question only a
/// technician can answer. "My laptop wakes up in my bag" is a question anyone can
/// answer, and it selects the same modules.
/// </summary>
public sealed record Playbook
{
    public required string Id { get; init; }

    /// <summary>The symptom in the user's words.</summary>
    public required string Title { get; init; }

    /// <summary>What WinVitals will do about it.</summary>
    public required string Subtitle { get; init; }

    /// <summary>Collector ids to run. Empty means every module.</summary>
    public required IReadOnlyList<string> Modules { get; init; }

    /// <summary>Roughly how long this takes, for setting expectations before the spinner starts.</summary>
    public required string Duration { get; init; }
}

public static class Playbooks
{
    public static readonly IReadOnlyList<Playbook> All = new[]
    {
        new Playbook
        {
            Id = "full",
            Title = "Give my PC a full check-up",
            Subtitle = "Every check WinVitals has. Start here if you are not sure.",
            Modules = Array.Empty<string>(),
            Duration = "about a minute",
        },
        new Playbook
        {
            Id = "slow",
            Title = "My PC is slow",
            Subtitle = "Startup programs, memory, disk health and free space, and anything crashing in the background.",
            Modules = new[] { "system", "storage", "memory", "startup", "devices", "reliability" },
            Duration = "about 40 seconds",
        },
        new Playbook
        {
            Id = "sleep",
            Title = "It won't sleep, or it wakes up on its own",
            Subtitle = "What is holding it awake, what is scheduled to wake it, and whether sleep is holding.",
            Modules = new[] { "power", "startup", "devices" },
            Duration = "about 20 seconds",
        },
        new Playbook
        {
            Id = "battery",
            Title = "The battery doesn't last",
            Subtitle = "Real battery wear against its original capacity, plus what is draining it.",
            Modules = new[] { "battery", "power", "startup" },
            Duration = "about 20 seconds",
        },
        new Playbook
        {
            Id = "network",
            Title = "Internet or Wi-Fi problems",
            Subtitle = "Adapters, drivers, DNS, and what this machine is listening for.",
            Modules = new[] { "network", "devices", "security" },
            Duration = "about 20 seconds",
        },
        new Playbook
        {
            Id = "crash",
            Title = "It crashes, freezes or restarts by itself",
            Subtitle = "Blue screens, hardware errors, memory, disk health and pending updates.",
            Modules = new[] { "reliability", "memory", "storage", "devices", "updates" },
            Duration = "about 40 seconds",
        },
        new Playbook
        {
            Id = "space",
            Title = "I'm running out of disk space",
            Subtitle = "What is actually using the space, including the Windows features that hide it.",
            Modules = new[] { "storage" },
            Duration = "about 20 seconds",
        },
        new Playbook
        {
            Id = "security",
            Title = "Is this PC safe?",
            Subtitle = "Antivirus, firewall, encryption, shared folders, remote access and patch level.",
            Modules = new[] { "security", "updates", "network" },
            Duration = "about 30 seconds",
        },
    };

    public static Playbook Full => All[0];

    public static Playbook? ById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
