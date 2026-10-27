# AgentAutoReply

A Windows desktop monitor for Codex CLI and Claude Code. Select multiple
sessions to monitor both tools at once.

- Codex: approve recognized single-use confirmation prompts, continue idle
  sessions, and resume after transient failures.
- Claude Code: retry transient API/network failures after built-in retries
  finish. No automatic approvals or idle continuation.
- Shared prompt pool and a separate send limit for each session.
- Minimize to the system tray and receive background turn-completion notices.

## Download

Download `AgentAutoReply-win-x64.zip` from
[Releases](https://github.com/LuckyHangMei/AgentAutoReply/releases), extract it,
and run `CodexAutoYes.exe`. The package includes the .NET runtime; no SDK or
separate runtime installation is needed. Releases are not code-signed.

Run Codex or native Claude Code in a Windows terminal, refresh the session
list, select the sessions, then start monitoring. If a target terminal runs
as administrator, the monitor needs the same permissions.

Only native Windows `codex.exe` and `claude.exe` processes are discovered.
Node-hosted and WSL sessions are not supported. Detection uses terminal text,
so CLI output changes may require updated matching rules. Claude Code
success notifications have not been validated against a live Claude session.
Automatic Codex approvals can execute commands or change files; select only
sessions you intend to authorize.

## Build And Test

Requires Windows x64 and the .NET 10 SDK.

```powershell
dotnet build tests/CodexAutoYes.Tests.csproj -c Release
& .\tests\bin\Release\net10.0-windows\CodexAutoYes.Tests.exe --ci
& .\bin\Release\net10.0-windows\CodexAutoYes.exe
```

`--ci` runs 19 checks without an interactive desktop or live CLI sessions.
Omit it to run the full local suite, which includes GUI, isolated console,
and live-session discovery tests. `--filter "turn:"` selects tests by name.
Reports are written to `artifacts/test-results.txt`.

`start.cmd` launches the copy in `artifacts` and only builds it when missing.
To update that local copy, stop it first and run:

```powershell
dotnet publish CodexAutoYes.csproj -c Release --no-self-contained -o artifacts
```

## Automatic Releases

Pushes to `main` and pull requests build, run the CI checks, and produce a
self-contained Windows x64 package. Pushing a version tag also publishes
the package, its SHA-256 checksum, and generated notes to GitHub Releases:

```powershell
git tag -a v0.1.1 -m "Release v0.1.1"
git push origin v0.1.1
```

GitHub Actions uses the repository's built-in `GITHUB_TOKEN`; no personal
access token is required. Desktop/console integration tests remain local
checks and are not represented as passing by the CI subset.
