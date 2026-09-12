# WinVitals

A read-only check-up for a Windows laptop. It looks at the whole machine, then writes
one self-contained HTML report that explains, in plain English, what it found, why each
thing matters, and what you can do about it.

It does not change anything on your computer.

```
WinVitals.exe
```

That is the whole thing. Double-click it, accept the elevation prompt, and a report
opens in your browser.

---

## Why this exists

The information needed to explain a misbehaving Windows laptop already exists on the
machine. It is just scattered across `powercfg`, Device Manager, the event log, WMI and
half a dozen Settings pages, several of which need administrator rights and none of
which talk to each other.

So when a laptop wakes up in a bag, or will not sleep, or has quietly filled its second
drive, the answer is usually a forum thread telling you to run four commands and
interpret the output yourself.

WinVitals runs those commands, joins them up, and writes down what they mean.

## What it checks

| Module | What it looks at |
|---|---|
| **System & Firmware** | Model, BIOS/UEFI version and date, Windows build, uptime, Fast Startup |
| **Power & Sleep** | Sleep states, what is blocking sleep right now, scheduled wake-ups, devices armed to wake, and whether sleep actually holds |
| **Storage** | Disk health and SMART, free space, TRIM, and which Windows features are eating the space |
| **Battery** | Real wear against design capacity, cycle count |
| **Memory** | Installed memory, slot arrangement, single-channel detection, current pressure, page file |
| **Devices & Drivers** | Every device Windows has flagged, with the error code translated |
| **Startup & Tasks** | What launches at sign-in, and which scheduled tasks are allowed to wake the machine |
| **Network** | Adapters and driver versions, DNS, and which ports are open to the network |
| **Security** | Antivirus, firewall per profile, BitLocker, shared folders, Remote Desktop |
| **Updates & Recovery** | Patch level, deferred restarts, and whether a usable restore point exists |
| **Crashes & Stability** | Blue screens, unexpected shutdowns, hardware errors, repeat app crashes, disk errors |

## Three rules it follows

These are the reasons to use this rather than a "PC health" tool.

**1. It never says something is out of date unless it knows.**

WinVitals will not tell you a BIOS or a driver is old. It reports the version and date
it found and links the vendor's own download page. Offline guesses about what is current
are wrong often enough to send people hunting for updates that do not exist, or to
"update" a driver to something older and generic. A 2022 driver is frequently the last
one the vendor ever shipped.

**2. It never confuses "I could not look" with "there is nothing there."**

Several checks — what is blocking sleep, disk SMART data, whether you have a restore
point — return access-denied to a standard user. A tool that swallows that error reports
a clean bill of health for a machine it never examined. Every check that could not run
says so, and the report counts them separately.

**3. It never changes anything.**

Where a fix exists, WinVitals shows you the exact command and, whenever that command
changes a setting, the command that puts it back. You run it. Nothing is applied for
you, and nothing is applied silently.

## Sharing a report

Reports end up pasted into forum threads and support tickets. So:

```
WinVitals.exe --redact
```

replaces your username, machine name, hardware serial, MAC and IP addresses with
placeholders. It is a best-effort text filter rather than a guarantee, and the report
says so at the bottom — give it a skim before posting.

## Options

```
--out <path>     Where to write the HTML report (default: your Desktop)
--json <path>    Also write the findings as JSON
--redact         Strip identifying details so the report is safe to share
--only <ids>     Run only these modules, comma separated
--skip <ids>     Run everything except these modules
--no-open        Do not open the report when finished
--no-elevate     Do not ask for administrator rights
--version        Print version
--help           Full help
```

Module ids: `system power storage battery memory devices startup network security
updates reliability`

Exit codes: `0` nothing critical, `1` at least one critical finding, `2` bad arguments,
`3` could not write the report.

## Administrator rights

WinVitals asks for elevation and will run without it if you say no.

It asks because these checks return nothing at all to a standard user:

- what is holding the machine awake right now (`powercfg /requests`)
- what is scheduled to wake it (`powercfg /waketimers`)
- the drive's own failure prediction (SMART)
- BitLocker status
- whether any restore point exists

Decline and you still get a useful scan. The report will tell you exactly which checks
were skipped, and the summary counts them under "not checked".

## Building it yourself

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/<you>/winvitals
cd winvitals
./build.ps1
```

The single-file executable lands in `dist/WinVitals.exe`. It is self-contained, so it
runs on a machine with no .NET installed.

For a normal debug build:

```
dotnet run --project src/WinVitals -- --no-elevate --no-open --out report.html
```

Tests:

```
dotnet test tests/WinVitals.Tests
```

The parser tests are pinned against verbatim `powercfg` output captured from real
machines, tabs and all. These formats are undocumented and vary by sleep model, so
they are tested against reality rather than against what the documentation implies.

## Adding a check

Collectors are independent and small. Implement `ICollector`, add it to the array in
`Program.cs`, and you are done.

Every finding carries four things, and a pull request that omits any of them will be
asked for it:

- **What** is literally true on this machine
- **Why** that matters, so the reader can decide whether to care
- **Action** — what to do, concretely, or nothing if there is nothing to do
- **Evidence** — the raw output the claim came from

The evidence requirement is the important one. WinVitals does not ask to be trusted; it
shows its working.

## Contributing

Issues and pull requests welcome. Especially useful:

- Machines where a check reports something wrong — please attach a `--redact`ed report
- Hardware where WMI returns something unexpected (OEM firmware is endlessly creative)
- Checks that would have saved you an afternoon

## Licence

MIT. See [LICENSE](LICENSE).
