using System.Diagnostics;
using System.IO.Enumeration;

namespace VorniskCli.Core;

/// <summary>
/// Cross-platform copy/move engine. Pure managed I/O (FileStream) — no P/Invoke, runs on Linux,
/// macOS, and Windows. Streaming double-buffered copy with inline xxHash3, optional verify pass,
/// token-bucket throttle, conflict handling (incl. interactive Ask via ConflictPrompt), excludes,
/// dry-run, cooperative pause, and timestamp/Unix-permission preservation.
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
        var sw = Stopwatch.StartNew();
        var plan = BuildPlan(opt);
        var items = plan.Items;

        // Dry-run: report the plan, touch nothing. OnItemCompleted fires per planned item (status
        // Pending) so a -v listing can show exactly what WOULD happen.
        if (opt.DryRun)
        {
            int conflicts = 0;
            foreach (var it in items)
            {
                if (File.Exists(it.DestPath)) conflicts++;
                Notify(opt, it);
            }
            sw.Stop();
            return new CopyResult
            {
                TotalFiles      = items.Count,
                PlannedBytes    = plan.TotalBytes,
                Conflicts       = conflicts,
                FilesExcluded   = plan.Excluded,
                SymlinksSkipped = plan.SymlinksSkipped,
                DryRun          = true,
                Elapsed         = sw.Elapsed,
            };
        }

        // Pre-create the destination directory tree (preserves empty source dirs). Best-effort: a bad
        // entry here (a file in the way, perms, path length, read-only mount) must NOT abort a 500 GB
        // job — each file recreates its own parent dir in ProcessItemAsync, where the failure is caught
        // and reported per-file instead of crashing the process.
        foreach (var d in plan.DestDirs)
            try { Directory.CreateDirectory(d); } catch { /* per-file CreateDirectory will surface it */ }

        var limiter = new TokenBucketRateLimiter(opt.MaxBytesPerSec <= 0 ? 0 : opt.MaxBytesPerSec);
        var state   = new RunState();

        long totalBytes   = plan.TotalBytes;
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
                    // Cooperative pause: also gated per-chunk inside CopyFileAsync/HashFileAsync,
                    // so latency is bounded by one buffer write either way.
                    if (opt.Pause is { } pg) await pg.WaitIfPausedAsync(c).ConfigureAwait(false);
                    try
                    {
                        await ProcessItemAsync(item, opt, limiter, state,
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
                        Notify(opt, item);
                    }
                    catch (OperationCanceledException) { throw; } // cancel is not a terminal item state — no notify
                    catch (Exception ex)
                    {
                        item.Status = ItemStatus.Failed;
                        item.Error  = ex.Message;
                        Interlocked.Increment(ref filesFailed);
                        lock (errors) errors.Add((item.SourcePath, ex.Message));
                        Notify(opt, item);
                    }
                }).ConfigureAwait(false);
        }

        // MOVE: remove the now-emptied source directory tree, bottom-up — `mv folder dest` should not
        // leave an empty skeleton behind. Empties-only by construction: a dir keeping ANY entry
        // (skipped/failed/excluded file, symlink we refused to follow, foreign file) stays, and so do
        // its ancestors. Never runs on cancel (user aborted — don't surprise-delete anything).
        int dirsRemoved = 0, dirsKept = 0;
        if (opt.Operation == CopyOperation.Move && !ct.IsCancellationRequested && plan.SourceDirRoots.Count > 0)
            (dirsRemoved, dirsKept) = CleanupSourceDirs(plan.SourceDirRoots);

        Emit();
        sw.Stop();

        var result = new CopyResult
        {
            TotalFiles        = items.Count,
            FilesCopied       = filesDone,
            FilesSkipped      = filesSkipped,
            FilesFailed       = filesFailed,
            FilesExcluded     = plan.Excluded,
            SymlinksSkipped   = plan.SymlinksSkipped,
            SourceDirsRemoved = dirsRemoved,
            SourceDirsKept    = dirsKept,
            BytesCopied       = Interlocked.Read(ref bytesDone),
            Elapsed           = sw.Elapsed,
        };
        result.Errors.AddRange(errors);
        return result;
    }

    private static void Notify(CopyOptions opt, FileItem item)
    {
        try { opt.OnItemCompleted?.Invoke(item); } catch { /* subscriber fault is non-fatal */ }
    }

    // ── planning ──────────────────────────────────────────────────────────────────

    private sealed record PlanResult(
        List<FileItem> Items, List<string> DestDirs, List<string> SourceDirRoots,
        long TotalBytes, int SymlinksSkipped, int Excluded);

    /// <summary>Per-run mutable state shared by workers (conflict-prompt serialization + apply-to-all cache).</summary>
    private sealed class RunState
    {
        public readonly SemaphoreSlim ConflictGate = new(1, 1);
        public ConflictMode? ApplyAll;
    }

    private static PlanResult BuildPlan(CopyOptions opt)
    {
        var items    = new List<FileItem>();
        var destDirs = new HashSet<string>();
        var srcRoots = new List<string>();
        long total   = 0;
        int symlinks = 0;
        int excluded = 0;

        bool destIsExistingDir = Directory.Exists(opt.Destination);
        bool container = destIsExistingDir
            || opt.Sources.Count > 1
            || opt.Sources.Any(Directory.Exists)
            || opt.Destination.EndsWith(Path.DirectorySeparatorChar)
            || opt.Destination.EndsWith('/');

        // Manual per-level walk (not RecurseSubdirectories) for three reasons:
        // SEC — symlinks/reparse points are seen, COUNTED, and never followed (recursion would
        //       otherwise silently drop them or, worse, follow into /etc or a cycle);
        // EXCLUDES — a matching directory is pruned before its subtree is ever walked;
        // RESILIENCE — one unreadable directory skips that level only, not the whole plan.
        // AttributesToSkip=None so hidden/system files still copy (matches prior behavior).
        var levelOpts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip      = FileAttributes.None,
            IgnoreInaccessible    = true,
        };

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

                srcRoots.Add(full);
                destDirs.Add(destRoot);

                var stack = new Stack<(string dir, string destDir)>();
                stack.Push((full, destRoot));
                while (stack.Count > 0)
                {
                    var (dir, destDir) = stack.Pop();
                    IEnumerable<FileSystemInfo> entries;
                    try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", levelOpts); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { continue; } // partial tree captured; the copy proceeds with what we have

                    foreach (var e in entries)
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            symlinks++; // seen, reported, never followed
                            continue;
                        }
                        var rel = Path.GetRelativePath(full, e.FullName);
                        if (IsExcluded(opt.Excludes, e.Name, rel))
                        {
                            excluded++; // a pruned dir counts once; its subtree is never walked
                            continue;
                        }
                        if (e is DirectoryInfo di)
                        {
                            var subDest = Path.Combine(destDir, di.Name);
                            destDirs.Add(subDest);
                            stack.Push((di.FullName, subDest));
                        }
                        else if (e is FileInfo fi)
                        {
                            long len = fi.Exists ? SafeLength(fi.FullName) : 0;
                            total += len;
                            items.Add(new FileItem { SourcePath = fi.FullName, DestPath = Path.Combine(destDir, fi.Name), SizeBytes = len });
                        }
                    }
                }
            }
            else if (File.Exists(source))
            {
                // Explicit file argument: excludes still apply (rsync semantics), symlink is followed
                // deliberately (naming the link IS user intent, unlike finding one mid-walk).
                if (IsExcluded(opt.Excludes, Path.GetFileName(source), Path.GetFileName(source)))
                {
                    excluded++;
                    continue;
                }
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

        return new PlanResult(items, destDirs.ToList(), srcRoots, total, symlinks, excluded);
    }

    /// <summary>Glob match: pattern with a path separator matches the source-relative path; otherwise the entry name.</summary>
    private static bool IsExcluded(IReadOnlyList<string> patterns, string name, string relPath)
    {
        if (patterns.Count == 0) return false;
        foreach (var p in patterns)
        {
            if (p.Contains('/') || p.Contains('\\'))
            {
                if (FileSystemName.MatchesSimpleExpression(p.Replace('\\', '/'), relPath.Replace('\\', '/'), ignoreCase: true))
                    return true;
            }
            else if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))
            {
                return true;
            }
        }
        return false;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>
    /// MOVE cleanup: delete emptied source dirs, deepest-first. Within one tree a child path is
    /// always longer than its parent, so a length sort gives valid bottom-up order. Deletion is
    /// empties-only and best-effort; anything still holding an entry (or throwing) is kept.
    /// Enumeration skips reparse points — we never walk through a symlink to judge emptiness.
    /// </summary>
    private static (int removed, int kept) CleanupSourceDirs(IReadOnlyList<string> roots)
    {
        int removed = 0, kept = 0;
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip      = FileAttributes.ReparsePoint,
            IgnoreInaccessible    = true,
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var dirs = new List<string> { root };
            try { dirs.AddRange(Directory.EnumerateDirectories(root, "*", enumOpts)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* judge what we found */ }

            dirs.Sort(static (a, b) => b.Length.CompareTo(a.Length)); // deepest first
            foreach (var d in dirs)
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        Directory.Delete(d);
                        removed++;
                    }
                    else kept++;
                }
                catch { kept++; } // locked/raced — leave it, never force
            }
        }
        return (removed, kept);
    }

    // ── per-item ──────────────────────────────────────────────────────────────────

    private async Task ProcessItemAsync(FileItem item, CopyOptions opt, TokenBucketRateLimiter limiter,
        RunState state, Action<long> onBytes, CancellationToken ct)
    {
        item.Status = ItemStatus.Copying;
        if (!File.Exists(item.SourcePath))
            throw new FileNotFoundException("Source not found", item.SourcePath);

        Directory.CreateDirectory(Path.GetDirectoryName(item.DestPath)!);

        var finalDest = item.DestPath;
        var resolved  = opt.Conflict;
        if (File.Exists(finalDest))
        {
            resolved = await ResolveConflictAsync(item, opt, state, ct).ConfigureAwait(false);
            switch (resolved)
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
                File.Move(item.SourcePath, finalDest, overwrite: resolved == ConflictMode.Overwrite);
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
            var srcHash = await CopyFileAsync(item.SourcePath, temp, limiter, opt.BufferSize, opt.Pause, onBytes, ct).ConfigureAwait(false);

            if (opt.Verify)
            {
                item.Status = ItemStatus.Verifying;
                var destHash = await HashFileAsync(temp, opt.BufferSize, opt.Pause, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Resolve an Ask conflict via the front-end prompt. Prompts are serialized (one modal at a
    /// time); an ApplyToAll answer is cached for the rest of the run. No prompt wired → Skip (safe).
    /// </summary>
    private static async Task<ConflictMode> ResolveConflictAsync(FileItem item, CopyOptions opt, RunState state, CancellationToken ct)
    {
        if (opt.Conflict != ConflictMode.Ask) return opt.Conflict;
        if (opt.ConflictPrompt == null) return ConflictMode.Skip;

        await state.ConflictGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.ApplyAll is { } cached) return cached;
            var d = await opt.ConflictPrompt(item, ct).ConfigureAwait(false);
            var mode = d.Mode == ConflictMode.Ask ? ConflictMode.Skip : d.Mode; // a prompt may not answer "Ask"
            if (d.ApplyToAll) state.ApplyAll = mode;
            return mode;
        }
        finally { state.ConflictGate.Release(); }
    }

    /// <summary>Double-buffered async copy: read chunk N+1 while writing chunk N. Hashes + throttles inline.</summary>
    private static async Task<byte[]> CopyFileAsync(string src, string temp, TokenBucketRateLimiter limiter,
        int bufSize, PauseGate? pause, Action<long> onBytes, CancellationToken ct)
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
            if (pause != null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
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

    private static async Task<byte[]> HashFileAsync(string path, int bufSize, PauseGate? pause, CancellationToken ct)
    {
        var hasher = new XxHash3Hasher();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buf = new byte[bufSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, bufSize), ct).ConfigureAwait(false)) > 0)
        {
            if (pause != null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            hasher.Append(buf.AsSpan(0, n));
        }
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
