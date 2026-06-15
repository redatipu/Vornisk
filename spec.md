# spec.md — Vornisk CLI (Linux / cross-platform edition)

## 1. Current State

- **Status:** v0.1.0 — functional MVP (CLI **+ GUI**), **security-hardened**. Build 0/0, tests **9/9**. Linux x64 CLI + GUI binaries published + ELF-verified.
- **What it is:** a copy/move tool for admins — a **CLI** (`vornisk`) and a **GUI** (`vornisk-gui`,
  Avalonia). Separate edition from the Windows engine (Win32/`net8.0-windows`, can't run on Linux);
  reimplements the I/O layer with portable `FileStream` and reuses the portable throttle + xxHash3.
- **Location:** `D:\Claude\Vornisk\FOR LINUX\`
- **Solution:** `VorniskCli.sln` → `src/VorniskCli.Core` (engine, `net8.0`), `src/VorniskCli`
  (console, `AssemblyName=vornisk`), `src/VorniskGui` (Avalonia desktop, `AssemblyName=vornisk-gui`),
  `tests/VorniskCli.Tests` (xUnit). Both front-ends share the one `CopyEngine`.
- **Build:** `dotnet build VorniskCli.sln`; Linux binaries via `scripts/build-linux.ps1` (CLI) and
  `scripts/build-linux-gui.ps1` (GUI) — self-contained single-file, no .NET on host. RIDs: linux-x64
  (built), linux-arm64 / musl supported.
- **Artifacts (self-contained single-file, ELF + arch verified):**
  - CLI: `dist/linux-x64/vornisk` 34.5 MB (x86-64) · `dist/linux-arm64/vornisk` 32.9 MB (AArch64)
  - GUI: `dist/linux-x64-gui/vornisk-gui` 38.2 MB (x86-64) · `dist/linux-arm64-gui/vornisk-gui` 36.6 MB (AArch64)
- **GUI:** Avalonia 11.2 + Fluent **dark** theme, references the Windows WPF layout but **compact**
  (560×640, min 440×540). MVVM (`MainViewModel`) over the shared engine. **Responsive / no-crop by
  construction:** Auto-width labels, `WrapPanel` option/action rows (wrap instead of clip), `TextWrapping=Wrap`
  on all status/path text (no trimming), whole UI inside a `ScrollViewer`. Source has **File…** and
  **Folder…** pickers; both Source + Destination accept **drag-and-drop** (`DragDrop` + `GetFiles()`).
  Pickers via Avalonia `StorageProvider`. Smoke-launched on Windows (Avalonia is cross-platform) — renders, no truncation.
- **Isolation:** own `Directory.Build.props` (does NOT import the parent Windows/WPF/DI one — MSBuild
  stops at the first found walking up).

## 2. Design decisions

- **No P/Invoke** — pure `FileStream`/`System.IO`, so the same code runs Linux/macOS/Windows.
- **Streaming + verify:** double-buffered async copy (read N+1 while writing N) with inline xxHash3;
  optional re-read verify pass; write to `*.part` temp then atomic `File.Move` promote.
- **Move:** same-FS no-verify = `rename(2)` fast path; cross-FS (EXDEV `IOException`) → copy-verify-delete;
  source deleted only after verified promote.
- **Throttle:** `TokenBucketRateLimiter` (ported verbatim, pure managed) driven by `--limit` only.
  No AIMD/idle-probe (the Windows GUI's load-aware tuning is meaningless headless).
- **Preserve:** mtime + btime (best-effort) + `UnixFileMode` permission bits (off-Windows only).
- **CLI:** dependency-free manual arg parser (no `System.CommandLine`) — keeps the binary lean and
  egress-free for locked-down/DMZ servers. Progress on stderr (`\r`), summary on stdout, `--json` mode.
- **Counting/visibility:** missing sources are planned + reported Failed (never silently dropped —
  mirrors the Windows R8-3 lesson); skipped files subtract from the progress denominator.

## 3. Completed

| Date | Task |
|---|---|
| 2026-06-15 | **GitHub publishing prep.** New human-voiced public `README.md` (no inspiration-tool mention); old README renamed `INT-README.md` (internal reference, kept). Added `LICENSE` (Apache-2.0, © 2026 redatipu) and `.gitignore` (.NET + `dist/` + binaries + IDE/OS). Target repo `github.com/redatipu/vornisk`; binaries distributed via GitHub Releases (gitignored, not committed). No code change. |
| 2026-06-15 | Created cross-platform CLI edition under `FOR LINUX/`. Engine (`CopyEngine`, `TokenBucketRateLimiter`, `XxHash3Hasher`, models) + `vornisk` console front-end (copy/move, verify, throttle, 3 conflict modes, threads, preserve, quiet/json). 8 xUnit tests (Windows-run, portable APIs). Linux x64 self-contained single-file published (34.5 MB, ELF-verified). Build 0/0, 8/8. |
| 2026-06-15 | **GUI: file picker + drag-and-drop; arm64 builds.** Source gains **File…** + **Folder…** pickers (was folder-only); Source + Destination boxes accept **drag-and-drop** of a file/folder (`DragDrop.AllowDrop` + DragOver/Drop, `e.Data.GetFiles()`). Published AArch64 binaries: `dist/linux-arm64/vornisk` (32.9 MB) + `dist/linux-arm64-gui/vornisk-gui` (36.6 MB); x64 GUI republished with the new pickers (38.2 MB). All four ELF + arch verified. Build 0/0, 9/9. |
| 2026-06-15 | **GUI added (`src/VorniskGui`, Avalonia 11.2).** Cross-platform desktop front-end over the shared `CopyEngine`, referencing the Windows WPF design but compact (560×640, min 440×540), Fluent dark. MVVM (`MainViewModel`): source/dest + folder pickers (`StorageProvider`), Move/Verify, conflict combo, threads, limit, Start/Cancel, live progress bar + text, log, status. Responsive/no-crop by construction: Auto labels, `WrapPanel` rows, `TextWrapping=Wrap` everywhere (no trim), whole UI in a `ScrollViewer`. Build 0/0; smoke-launched on Windows (renders, no truncation). Published self-contained single-file `dist/linux-x64-gui/vornisk-gui` 38.2 MB (ELF-verified). `scripts/build-linux-gui.ps1`. |
| 2026-06-15 | **Security pass (MVP) — 4 fixes.** (1) Source symlink traversal: enumeration now skips `ReparsePoint` (no symlink-follow → no path-escape/cycle-DoS) + `IgnoreInaccessible`. (2) Temp TOCTOU: `*.part` opened `CreateNew` not `Create` (won't write through a planted symlink). (3) Priv-esc: strip setuid/setgid (`SetUser\|SetGroup`) before applying `UnixFileMode` (never mint a setuid copy, esp. under sudo). (4) `--json`: escape all control chars (`<0x20`) → no malformed/injectable JSON from crafted filenames. +1 symlink-no-follow test (self-skips where unprivileged). Build 0/0, 9/9. Binary republished. |

## 4. Limitations / not included (by design)

- Ownership (uid/gid) — needs root + `chown(2)`, not wired.
- ACLs / xattrs / hardlinks / symlink re-creation — regular files + dirs only; symlinks followed as targets.
- No `NO_BUFFERING`/`O_DIRECT` — uses buffered page cache (portable + safe on every FS incl. network mounts).

## 5. Remaining / next (optional)

- **Run the Linux binary on an actual Linux host** — published from Windows (cross-compiled), so the
  ELF is unexecuted here; functional tests ran on Windows via portable APIs. Validate on real Linux.
- Optional: `chown` preservation behind a `--preserve-owner` flag (Linux P/Invoke to `chown`), root-gated.
- Optional: progress `--json` streaming (line-per-tick) for orchestration.
- Optional: man page + `.deb`/`.rpm`/tarball packaging; arm64/musl builds.
