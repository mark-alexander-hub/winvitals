# Code signing

WinVitals releases are signed through **SignPath Foundation**, which provides free
code-signing certificates to open-source projects. Nothing about this involves the
user: it is a certificate attached to `WinVitals.exe` at build time so Windows can
name the publisher instead of showing *Unknown publisher*.

## How a release gets signed

1. A tag `vX.Y.Z` is pushed. GitHub Actions builds, tests and publishes the single-file
   executable and uploads it as an artifact.
2. The workflow submits that artifact to SignPath with the `release-signing` policy.
3. SignPath Foundation requires a human to approve each release signing. The
   maintainer approves it in the SignPath dashboard; the workflow waits up to an hour.
4. The signed executable comes back, replaces the unsigned one, and is attached to the
   GitHub release.

Builds on `main` and pull requests are not signed. Forks cannot sign: the secrets are
not available to them.

## One-time setup

The workflow step is already in `.github/workflows/build.yml` and activates only when
these exist in the repository settings:

| Where | Name | Value |
|---|---|---|
| Secrets | `SIGNPATH_API_TOKEN` | API token from SignPath (User → API tokens) |
| Variables | `SIGNPATH_ORGANIZATION_ID` | Organization ID shown in the SignPath dashboard |

The project slug is `winvitals` and the signing policy slug is `release-signing`; both
are created by SignPath when the application is approved and can be changed in the
workflow if they differ.

## Application

**Creating a SignPath.io account is not the application.** They are two separate
things, and doing only the first leaves you stuck: signing up gives you an empty
workspace whose certificate options are a self-signed certificate, a CSR for buying a
commercial one, or importing a PFX you already own.

None of those removes *Unknown publisher*. A self-signed certificate is not verified by
a certificate authority and has to be installed by hand on every machine that is meant
to trust it, so on a stranger's PC the warning is identical to having no certificate at
all. SignPath's own wording says to consider it "only for test-signing".

The certificate this project uses is **granted** by SignPath Foundation after they
review the application below, and appears under **Certificates** in the workspace on
its own. If you already have a SignPath.io organization, name it in the application so
they attach the certificate to it.

Apply at https://signpath.org/apply with:

> **Project name:** WinVitals
>
> **Repository:** https://github.com/mark-alexander-hub/winvitals
>
> **Licence:** MIT
>
> **Website:** https://github.com/mark-alexander-hub/winvitals
>
> **Description:** WinVitals is a diagnose, clean-up and speed-up tool for Windows PCs,
> written for people who are not technical. The user picks a symptom in plain words
> ("it won't sleep", "my PC is slow") and the tool runs the relevant checks, explains
> each finding in plain English with the evidence it was derived from, and offers
> repairs that show their exact commands first, take a verified system restore point,
> and can be undone from an undo journal. It ships as a single self-contained .NET 8
> executable. Because it must run as administrator to do its job, an unsigned binary
> triggers SmartScreen and "Unknown publisher" on every download, which stops exactly
> the non-technical users it is written for.
>
> **Build:** GitHub Actions, `windows-latest`, `.github/workflows/build.yml`. The release
> artifact is produced by `dotnet publish` in the same workflow run that submits it for
> signing. Every push builds and runs the test suite; tags `v*` publish a release.
>
> **Maintainer:** Mark Isidore Nicholas Alexander (GitHub: mark-alexander-hub)
>
> **Artifacts to sign:** `WinVitals.exe` (x64, self-contained single file)

## After approval

- Add the secret and variable above.
- Add the acknowledgement SignPath Foundation asks for to the README:
  *"Free code signing provided by SignPath.io, certificate by SignPath Foundation."*
- Push a tag; approve the signing request in the SignPath dashboard when it arrives.
- Check the file's properties on the release asset show a *Digital Signatures* tab.

SmartScreen reputation is earned separately from the signature and builds with
downloads over time; a signed file with no reputation may still see a softer warning
for a while.
