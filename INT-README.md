# Vornisk CLI (Linux / cross-platform)

A terminal copy/move tool for admins — fast, **verified** (xxHash3), throttleable. The cross-platform
sibling of the Windows Vornisk GUI. Pure managed I/O (no P/Invoke) → runs on Linux, macOS, Windows.

> This is a **separate edition**, not the Windows engine recompiled. The Windows engine is built on
> Win32 (`CreateFile`, `FILE_FLAG_NO_BUFFERING`, `DeviceIoControl`, NTFS ACL/ADS, `net8.0-windows`)
> and cannot run on Linux. This edition reimplements the I/O layer with `FileStream` and reuses the
> portable pieces (token-bucket throttle, xxHash3 verification).

There are two front-ends over the same engine: the **CLI** (`vornisk`) and a **desktop GUI**
(`vornisk-gui`, Avalonia — compact, dark, responsive). Use whichever fits the box (GUI needs a
desktop; CLI is ideal for headless servers / SSH / scripts).

## GUI (Avalonia)

Compact desktop window (560×640, min 440×540), Fluent dark, referencing the Windows layout. Source
(**File…** or **Folder…** picker) + Destination (folder picker), and **drag-and-drop** onto either box;
Move/Verify toggles, conflict mode, threads, throttle, Start/Cancel, live progress + log. Responsive by
construction — labels Auto-width, option/action rows wrap, all text wraps (never trimmed), whole UI in a
scroll view, so nothing clips at any size.

```bash
pwsh scripts/build-linux-gui.ps1                       # x64  → dist/linux-x64-gui/vornisk-gui
pwsh scripts/build-linux-gui.ps1 -Runtime linux-arm64 # arm64 → dist/linux-arm64-gui/vornisk-gui
chmod +x vornisk-gui && ./vornisk-gui                  # needs an X11/Wayland desktop + GUI libs
```

GUI host needs: a desktop session (X11 or Wayland), `fontconfig`, `libGL`, `libX11`/`libICE`/`libSM`
(present on any standard desktop distro). Headless servers → use the CLI.

## Build (CLI)

Self-contained single-file binary — **no .NET runtime needed** on the target host:

```bash
pwsh scripts/build-linux.ps1                 # → dist/linux-x64/vornisk
pwsh scripts/build-linux.ps1 -Runtime linux-arm64
```

Or directly:

```bash
dotnet publish src/VorniskCli/VorniskCli.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/linux-x64
```

Deploy:

```bash
chmod +x vornisk
sudo install -m 0755 vornisk /usr/local/bin/vornisk
vornisk --help
```

## Usage

```
vornisk [OPTIONS] SOURCE... DESTINATION
```

| Option | Meaning |
|---|---|
| `-m, --move` | move instead of copy (deletes source **after** verified write) |
| `--no-verify` | skip the xxHash3 verify pass (faster, less safe) |
| `-j, --threads N` | concurrent file workers (default: `min(4, CPUs)`) |
| `-l, --limit RATE` | throttle, e.g. `200M`, `1G`, `512K` (base-1024 B/s; default unlimited) |
| `--on-conflict MODE` | `skip` \| `overwrite` \| `rename` (default `skip`) |
| `--overwrite` | shorthand for `--on-conflict overwrite` |
| `--buffer SIZE` | I/O buffer (default `1M`) |
| `--no-preserve` | don't preserve timestamps / Unix permissions |
| `-q, --quiet` | no progress, no summary |
| `--json` | machine-readable summary on stdout |
| `-h/--help`, `-v/--version` | |

Exit codes: `0` ok · `1` ≥1 file failed · `2` usage error · `130` cancelled (Ctrl-C).

### Examples

```bash
vornisk /data/file.iso /backup/                       # verified copy into a dir
vornisk -m --on-conflict rename /src/a /src/b /dst    # move two trees, auto-rename clashes
sudo vornisk -j 8 -l 300M /mnt/array /mnt/nas/backup  # throttled, 8 workers, to a NAS mount
vornisk --json --quiet a.bin /b | jq .success         # scriptable
```

## Guarantees & behaviour

- **Verify on by default:** each file is hashed (xxHash3) while read, then the written copy is
  re-read and hashed; mismatch → that file fails, source is left intact.
- **Atomic promote:** writes to a sibling `*.part` temp, then renames into place. A crash never
  leaves a half-written file at the final path.
- **Move safety:** source is deleted only after the destination is written **and** verified.
  Same-filesystem move without `--verify` uses an instant `rename(2)`; cross-filesystem falls back
  to copy-verify-delete automatically.
- **Preserves:** mtime, btime (best-effort), and Unix permission bits (`UnixFileMode`).

## Not included (by design — scope)

- **Ownership (uid/gid)** preservation — needs root + `chown(2)`; not wired. Run as the owning user
  or re-`chown` after if needed.
- **ACLs / xattrs / hardlinks / symlink re-creation** — not handled (regular files + dirs only;
  symlinks are followed as their targets).
- **Adaptive (AIMD) throttle** — the Windows GUI tunes by disk-queue/idle; meaningless headless.
  Here throttle is the explicit `--limit` ceiling only.

## Tests

```bash
dotnet test VorniskCli.sln          # 8 tests (engine): copy, tree, move, 3 conflict modes, missing-source, rate-limit
```
