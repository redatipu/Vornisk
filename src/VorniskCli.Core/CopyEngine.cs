using System.Diagnostics;

namespace VorniskCli.Core;

/// <summary>
/// Cross-platform copy/move engine. Pure managed I/O (FileStream) — no P/Invoke, runs on Linux,
/// macOS, and Windows. Streaming double-buffered copy with inline xxHash3, optional verify pass,
/// token-bucket throttle, conflict handling, and timestamp/Unix-permission preservation.
///
/// Differences from the Windows GUI engine (by design): no FILE_FLAG_NO_BUFFERING (Linux uses the
/// page cache / O_DIRECT differently; portable buffered I/O is used), no NTFS ACL/ADS (NTFS-only),
/// no IOCTL drive profiling or input-idle AIMD (meaningless on a headless server) — throttle here
/// is the explicit --limit ceiling only.
/// </summary>
public sealed class CopyEngine
{
    public async Task<CopyResult> RunAsync(CopyOptions opt, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var (items, destDirs, plannedBytes) = BuildPlan(opt);

        // Pre-create the destination directory tree (preserves empty source dirs). Best-effort: a bad
        // entry here (a file in the way, perms, path length, read-only mount) must NOT abort a 500 GB
        // job — each file recreates its own parent dir in ProcessItemAsync, where the failure is caught
        // and reported per-file instead of crashing the process.
        foreach (var d in destDirs)
            try { Directory.CreateDirectory(d); } catch { /* per-file CreateDirectory will surface it */ }

        var limiter = new TokenBucketRateLimiter(opt.MaxBytesPerSec <= 0 ? 0 : opt.MaxBytesPerSec);
        var sw = Stopwatch.StartNew();

        long totalBytes   = plannedBytes;
        long bytesDone    = 0;
        int  filesDone    = 0;
        int  filesFailed  = 0;
        int  filesSkipped = 0;
        FileItem? currentItem = null;          // most-recently-started in-flight file (drives the per-file bar)
        var errors = new List<(string, string)>();

        void Emit()
        {
            var ci = currentItem; // reference read is atomic; its BytesCopied has a single writer (its worker)
            progress?.Report(new CopyProgress(
                Interlocked.Read(ref totalBytes), Interlocked.Read(ref bytesDone),
                items.Count, Volatile.Read(ref filesDone), Volatile.Read(ref filesFailed),
                Volatile.Read(ref filesSkipped),
                ci?.SourcePath, ci?.BytesCopied ?? 0, ci?.SizeBytes ?? 0,
                sw.Elapsed.TotalSeconds));
        }

        using (var timer = new Timer(_ => Emit(), null, 200, 200))
        {
            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opt.Threads), CancellationToken = ct },
                async (item, c) =>
                {
                    currentItem = item;
                    try
                    {
                        await ProcessItemAsync(item, opt, limiter,
                            // long, not int: the move fast path reports whole-file sizes (>2 GB files
                            // would truncate at int.MaxValue and stall the bar short of 100%).
                            (long n) => { Interlocked.Add(ref bytesDone, n); item.BytesCopied += n; }, c).ConfigureAwait(false);

                        if (item.Status == ItemStatus.Skipped)
                        {
                            Interlocked.Increment(ref filesSkipped);
                            Interlocked.Add(ref totalBytes, -item.SizeBytes); // keep the bar honest
                        }
                        else
                        {
                            Interlocked.Increment(ref filesDone);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        item.Status = ItemStatus.Failed;
                        item.Error  = ex.Message;
                        Interlocked.Increment(ref filesFailed);
                        lock (errors) errors.Add((item.SourcePath, ex.Message));
                    }
                }).ConfigureAwait(false);
        }

        Emit();
        sw.Stop();

        var result = new CopyResult
        {
            TotalFiles   = items.Count,
            FilesCopied  = filesDone,
            FilesSkipped = filesSkipped,
            FilesFailed  = filesFailed,
            BytesCopied  = Interlocked.Read(ref bytesDone),
            Elapsed      = sw.Elapsed,
        };
        result.Errors.AddRange(errors);
        return result;
    }

    // ── planning ──────────────────────────────────────────────────────────────────

    private static (List<FileItem> items, List<string> destDirs, long totalBytes) BuildPlan(CopyOptions opt)
    {
        var items    = new List<FileItem>();
        var destDirs = new HashSet<string>();
        long total   = 0;

        bool destIsExistingDir = Directory.Exists(opt.Destination);
        bool container = destIsExistingDir
            || opt.Sources.Count > 1
            || opt.Sources.Any(Directory.Exists)
            || opt.Destination.EndsWith(Path.DirectorySeparatorChar)
            || opt.Destination.EndsWith('/');

        foreach (var raw in opt.Sources)
        {
            var source = raw;

            if (Directory.Exists(source))
            {
                var full     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                var name     = Path.GetFileName(full);
                // Single dir + non-existing dest → dest IS the copy (cp -r src newdir);
                // otherwise nest under dest (cp -r src existingdir/ → existingdir/src).
                var destRoot = (!destIsExistingDir && opt.Sources.Count == 1)
                    ? opt.Destination
                    : Path.Combine(opt.Destination, name);

                destDirs.Add(destRoot);
                // SEC: do NOT follow symlinks (ReparsePoint) during recursion. Following them lets a
                // crafted source tree escape the root (symlink → /etc), copy unintended data, or hang
                // on a symlink cycle. Matches the Windows engine's reparse-point skip. IgnoreInaccessible
                // so one unreadable entry doesn't abort the whole job.
                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip      = FileAttributes.ReparsePoint,
                    IgnoreInaccessible    = true,
                };
                // Resilient enumeration: IgnoreInaccessible already skips perm errors; this try also
                // stops a hard I/O error deep in the tree (flaky mount, vanished dir, path too long)
                // from aborting the *entire* plan — we keep what we walked and move on.
                // ponytail: a throw ends that subtree's walk here; fine unless you need every-file-or-fail
                // semantics (then enumerate eagerly per-dir and record each failure as a Failed item).
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(full, "*", enumOpts))
                        destDirs.Add(Path.Combine(destRoot, Path.GetRelativePath(full, dir)));

                    foreach (var file in Directory.EnumerateFiles(full, "*", enumOpts))
                    {
                        var rel  = Path.GetRelativePath(full, file);
                        var dest = Path.Combine(destRoot, rel);
                        long len = SafeLength(file);
                        total += len;
                        items.Add(new FileItem { SourcePath = file, DestPath = dest, SizeBytes = len });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // partial tree captured; the copy proceeds with what we have
                }
            }
            else if (File.Exists(source))
            {
                var dest = container ? Path.Combine(opt.Destination, Path.GetFileName(source)) : opt.Destination;
                long len = SafeLength(source);
                total += len;
                items.Add(new FileItem { SourcePath = source, DestPath = dest, SizeBytes = len });
            }
            else
            {
                // Missing source: still planned (never silently dropped — mirrors R8-3). ProcessItemAsync
                // throws FileNotFoundException for it → counted as Failed with an error message.
                var dest = container ? Path.Combine(opt.Destination, Path.GetFileName(source)) : opt.Destination;
                items.Add(new FileItem { SourcePath = source, DestPath = dest, SizeBytes = 0 });
            }
        }

        return (items, destDirs.ToList(), total);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ── per-item ──────────────────────────────────────────────────────────────────

    private async Task ProcessItemAsync(FileItem item, CopyOptions opt, TokenBucketRateLimiter limiter,
        Action<long> onBytes, CancellationToken ct)
    {
        item.Status = ItemStatus.Copying;
        if (!File.Exists(item.SourcePath))
            throw new FileNotFoundException("Source not found", item.SourcePath);

        Directory.CreateDirectory(Path.GetDirectoryName(item.DestPath)!);

        var finalDest = item.DestPath;
        if (File.Exists(finalDest))
        {
            switch (opt.Conflict)
            {
                case ConflictMode.Skip:
                    item.Status = ItemStatus.Skipped;
                    return;
                case ConflictMode.Rename:
                    finalDest = UniqueName(finalDest);
                    item.DestPath = finalDest;
                    break;
                case ConflictMode.Overwrite:
                    break;
            }
        }

        // Fast path: same-filesystem MOVE without verify = atomic rename, zero bytes streamed.
        if (opt.Operation == CopyOperation.Move && !opt.Verify)
        {
            try
            {
                File.Move(item.SourcePath, finalDest, overwrite: opt.Conflict == ConflictMode.Overwrite);
                item.Status = ItemStatus.Done;
                onBytes(item.SizeBytes); // full size — rename moved the whole file (was int-truncated at 2 GB)
                return;
            }
            catch (IOException)
            {
                // Cross-device (EXDEV) or overwrite race → fall through to streaming copy+delete.
            }
        }

        // Streaming copy to a sibling temp, then atomic promote.
        var temp = finalDest + ".vornisk-" + Guid.NewGuid().ToString("N") + ".part";
        bool ok = false;
        try
        {
            var srcHash = await CopyFileAsync(item.SourcePath, temp, limiter, opt.BufferSize, onBytes, ct).ConfigureAwait(false);

            if (opt.Verify)
            {
                item.Status = ItemStatus.Verifying;
                var destHash = await HashFileAsync(temp, opt.BufferSize, ct).ConfigureAwait(false);
                if (!srcHash.AsSpan().SequenceEqual(destHash))
                    throw new IOException($"Verification failed (hash mismatch): {item.SourcePath}");
            }

            ApplyMetadata(item.SourcePath, temp, opt);
            File.Move(temp, finalDest, overwrite: true);
            ok = true;

            if (opt.Operation == CopyOperation.Move)
                File.Delete(item.SourcePath); // only after dest is verified + promoted

            item.Status = ItemStatus.Done;
        }
        finally
        {
            if (!ok && File.Exists(temp))
                try { File.Delete(temp); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Double-buffered async copy: read chunk N+1 while writing chunk N. Hashes + throttles inline.</summary>
    private static async Task<byte[]> CopyFileAsync(string src, string temp, TokenBucketRateLimiter limiter,
        int bufSize, Action<long> onBytes, CancellationToken ct)
    {
        var hasher = new XxHash3Hasher();
        await using var inStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // SEC: CreateNew (not Create) — refuse to open an existing path as our temp. The temp name is
        // GUID-random, so a planted symlink/file at that exact path is implausible; CreateNew closes
        // the residual TOCTOU where Create would follow a pre-existing symlink and write through it.
        await using var outStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufSize, FileOptions.Asynchronous);

        byte[] a = new byte[bufSize];
        byte[] b = new byte[bufSize];

        int read = await inStream.ReadAsync(a.AsMemory(0, bufSize), ct).ConfigureAwait(false);
        while (read > 0)
        {
            hasher.Append(a.AsSpan(0, read));
            await limiter.ConsumeAsync(read, ct).ConfigureAwait(false);

            var writeTask = outStream.WriteAsync(a.AsMemory(0, read), ct);
            var readTask  = inStream.ReadAsync(b.AsMemory(0, bufSize), ct);
            await writeTask.ConfigureAwait(false);
            onBytes(read);
            read = await readTask.ConfigureAwait(false);

            (a, b) = (b, a);
        }

        await outStream.FlushAsync(ct).ConfigureAwait(false);
        return hasher.GetCurrentHash();
    }

    private static async Task<byte[]> HashFileAsync(string path, int bufSize, CancellationToken ct)
    {
        var hasher = new XxHash3Hasher();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buf = new byte[bufSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, bufSize), ct).ConfigureAwait(false)) > 0)
            hasher.Append(buf.AsSpan(0, n));
        return hasher.GetCurrentHash();
    }

    private static void ApplyMetadata(string src, string dest, CopyOptions opt)
    {
        try
        {
            if (opt.PreserveTimestamps)
            {
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                try { File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src)); } catch { /* some FS lack btime */ }
            }
            // UnixFileMode get/set is only meaningful (and supported) off-Windows.
            if (opt.PreservePermissions && !OperatingSystem.IsWindows())
            {
                // SEC: strip setuid/setgid before applying. Blindly propagating them onto a copy — whose
                // owner is the invoking user (root when run via sudo) — can mint a setuid binary in an
                // attacker-influenced location = privilege escalation. `cp` is conservative here too.
                var mode = File.GetUnixFileMode(src) & ~(UnixFileMode.SetUser | UnixFileMode.SetGroup);
                File.SetUnixFileMode(dest, mode);
            }
        }
        catch { /* metadata is best-effort — never fail a verified transfer over it */ }
    }

    private static string UniqueName(string path)
    {
        var dir  = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(cand)) return cand;
        }
    }
}
