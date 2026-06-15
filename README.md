# Vornisk

A copy/move tool for people who actually care whether the bytes arrived intact.

`cp` is fine until it isn't. It won't tell you a file got mangled in transit, it has no throttle when you're copying over a busy link, and watching a 200 GB move with no progress is its own kind of pain. Vornisk does the boring-but-important parts: it verifies every file it writes, it can rate-limit itself, it shows you what's happening, and it never deletes your source until the destination is confirmed good.

It ships as two front-ends over the same engine — a CLI (`vornisk`) for servers, SSH and scripts, and a small desktop GUI (`vornisk-gui`) for when you'd rather click. Both run on Linux, macOS and Windows. The binaries are self-contained, so there's no .NET runtime to install on the target box.

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-linux%20%7C%20macOS%20%7C%20windows-lightgrey.svg)

## Install (CLI)

Grab a binary from the [Releases](https://github.com/redatipu/vornisk/releases) page (pick `linux-x64` or `linux-arm64`), then drop it on your `PATH`:

```bash
chmod +x vornisk
sudo install -m 0755 vornisk /usr/local/bin/vornisk
vornisk --version
```

That's it — no runtime, no dependencies. If you'd rather build it yourself, see [Building](#building) below.

## Quick start

```bash
# verified copy of a file into a directory
vornisk /data/backup.tar.zst /mnt/nas/backups/

# move two trees, auto-rename anything that clashes at the destination
vornisk -m --on-conflict rename /srv/projectA /srv/projectB /archive/

# throttle to 300 MB/s with 8 workers when copying to a NAS over a shared link
vornisk -j 8 -l 300M /mnt/array /mnt/nas/backup

# scriptable: machine-readable summary, no chatter
vornisk --json --quiet a.bin /b | jq .success
```

While it runs you get two progress bars — one for the file it's currently on, one for the whole job — so you can see both at a glance. Pipe the output somewhere and it quietly drops down to a single plain status line.

By default Vornisk hashes every file as it reads it, then re-reads the written copy and compares. If they don't match, that file is marked failed and the source is left untouched. You can turn that off with `--no-verify` if you trust the path and want the speed.

## OPTIONS 

```
vornisk [OPTIONS] SOURCE... DESTINATION
```

| Option | What it does |
|---|---|
| `-m, --move` | Move instead of copy. Source is deleted only after the write is verified. |
| `--no-verify` | Skip the verify pass. Faster, less safe. |
| `-j, --threads N` | Concurrent file workers. Default: `min(4, CPU count)`. |
| `-l, --limit RATE` | Throttle, e.g. `512K`, `200M`, `1G` (base-1024 bytes/sec). Default: unlimited. |
| `--on-conflict MODE` | `skip` (default), `overwrite`, or `rename`. |
| `--overwrite` | Shorthand for `--on-conflict overwrite`. |
| `--buffer SIZE` | I/O buffer size. Default: `1M`. |
| `--no-preserve` | Don't carry over timestamps and Unix permission bits. |
| `-q, --quiet` | No progress, no summary. |
| `--json` | Machine-readable summary on stdout. |
| `-h, --help` / `-v, --version` | The usual. |

Exit codes: `0` success · `1` at least one file failed · `2` bad usage · `130` cancelled (Ctrl-C).

## GUI INSTALLATION

If you'd rather not type, `vornisk-gui` is a small dark-themed desktop window built on Avalonia. Pick a source file or folder (or just drag it onto the window), pick a destination, set move/verify/conflict/threads/throttle, and hit Start. There's a live progress bar and a log. It needs a desktop session (X11 or Wayland) and the usual graphics libs — on a headless server, stick to the CLI.

```bash
chmod +x vornisk-gui
./vornisk-gui
```

## How it works

A few things worth knowing about how it behaves:

- **Verify is on by default.** Each file is hashed with xxHash3 while being read, then the written copy is re-read and hashed. A mismatch fails that one file without touching the source.
- **Writes are atomic.** Data goes to a sibling `*.part` file first, then gets renamed into place. A crash or a pulled cable never leaves a half-written file sitting at the final path pretending to be complete.
- **Moves are safe.** The source is only removed after the destination is written *and* verified. A move within the same filesystem uses an instant `rename()`; across filesystems it falls back to copy-verify-delete automatically.
- **It preserves what it can.** Modification time, creation time (best effort) and Unix permission bits come along for the ride unless you pass `--no-preserve`.
- **It throttles honestly.** `--limit` is a hard ceiling enforced by a token bucket, so you can copy over a link someone else is using without saturating it.

## Building

You need the .NET 8 SDK. From the repo root:

```bash
# CLI, x64
dotnet publish src/VorniskCli/VorniskCli.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o dist/linux-x64

# CLI, arm64
dotnet publish src/VorniskCli/VorniskCli.csproj -c Release -r linux-arm64 \
  --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o dist/linux-arm64
```

There are convenience scripts too (PowerShell, runs anywhere `pwsh` does):

```bash
pwsh scripts/build-linux.ps1                       # CLI  → dist/linux-x64/vornisk
pwsh scripts/build-linux.ps1 -Runtime linux-arm64
pwsh scripts/build-linux-gui.ps1                   # GUI  → dist/linux-x64-gui/vornisk-gui
pwsh scripts/build-linux-gui.ps1 -Runtime linux-arm64
```

Run the tests with:

```bash
dotnet test VorniskCli.sln
```

## Limitations

Honest about what it doesn't do:

- **No ownership (uid/gid) preservation.** That needs root and `chown`, and it isn't wired up. Run as the owning user, or `chown` afterwards.
- **Regular files and directories only.** ACLs, extended attributes and hardlinks aren't reproduced, and symlinks are followed as their targets rather than recreated.
- **Buffered I/O only.** It uses the normal page cache rather than direct/unbuffered I/O, which keeps it portable and safe across every filesystem, including network mounts.

## License

[Apache License 2.0](LICENSE). Copyright © 2026 redatipu.
