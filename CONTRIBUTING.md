# Contributing to WinVitals

Thanks for looking. WinVitals is a small project and every contribution is read by a
person, usually within a day or two.

## Ways to help, smallest first

1. **Say hello.** Use the [hello form](https://github.com/mark-alexander-hub/winvitals/issues/new?template=hello.yml)
   or the *Say hello* button in the app. Knowing how people found it and what they fixed
   is the most useful thing you can send.
2. **Report a wrong result.** A check that says something untrue about your PC is the
   most valuable kind of bug. Attach a redacted report (`WinVitals.exe --redact`).
3. **Pick a [`good first issue`](https://github.com/mark-alexander-hub/winvitals/labels/good%20first%20issue).**
   These are scoped to one file or one check and say where to start.
4. **Add a check or a repair** that would have saved you an afternoon. Open an issue first
   so we can agree on what it should report.

## Setting up

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/<you>/winvitals
cd winvitals
dotnet test tests/WinVitals.Tests
dotnet run --project src/WinVitals.App -- --no-elevate --no-open --out report.html
```

`./build.ps1` makes the single-file `dist/WinVitals.exe`. Debug builds run without the
UAC prompt, so you can test the interface as a normal user; anything that needs
administrator rights will say it could not look rather than pretend.

## What a good pull request looks like

- **One change per pull request.** A new check, a fixed parser, a wording fix.
- **Findings carry What, Why, Action and Evidence.** See *Adding a check* in the
  [README](README.md#adding-a-check). The evidence is the raw output the claim came from.
- **Repairs capture undo before they change anything**, and set `Reversible = false`
  honestly. See *Adding a repair* in the README.
- **Parsers get a test pinned to real output.** Paste the verbatim command output into a
  test, tabs and all. Undocumented Windows formats are tested against reality.
- **Plain words in the interface.** The people using this are not technical. "Your
  laptop is set to wake up for updates at 3 am" beats "Wake timer armed".
- **Never claim something is out of date** unless the vendor says so. Report the version
  and link the vendor page.

CI builds and runs the tests on every pull request. If it is red and you can't see why,
say so in the pull request and we will look together.

## Code of conduct

Be kind, assume good faith, and keep criticism about the code. Maintainers may close or
lock anything that isn't.

## Licence

By contributing you agree your work is released under the [MIT licence](LICENSE).
