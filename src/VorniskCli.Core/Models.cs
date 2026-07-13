namespace VorniskCli.Core;

public enum CopyOperation { Copy, Move }

/// <summary>Ask requires <see cref="CopyOptions.ConflictPrompt"/>; without one it degrades to Skip (safe).</summary>
public enum ConflictMode { Skip, Overwrite, Rename, Ask }

public enum ItemStatus { Pending, Copying, Verifying, Done, Skipped, Failed }

/// <summary>One planned file transfer (source → resolved destination).</summary>
public sealed class FileItem
{
    public required string SourcePath { get; init; }
    public required string DestPath   { get; set; }
    public long            SizeBytes  { get; init; }
    /// <summary>Bytes written so far for THIS file (single-writer: its own worker). Drives the per-file bar.</summary>
    public long            BytesCopied { get; set; }
    public ItemStatus      Status     { get; set; } = ItemStatus.Pending;
    public string?         Error      { get; set; }
}

/// <summary>An interactive answer to a destination conflict (GUI "Ask" mode).</summary>
public readonly record struct ConflictDecision(ConflictMode Mode, bool ApplyToAll);

/// <summary>
/// Cooperative pause gate. Front-end holds the instance (Pause/Resume buttons); the engine awaits
/// it between chunks and items, so pause latency is bounded by one buffer write.
/// </summary>
public sealed class PauseGate
{
    private TaskCompletionSource? _tcs;
    public bool IsPaused => Volatile.Read(ref _tcs) != null;

    public void Pause() =>
        Interlocked.CompareExchange(ref _tcs,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    public void Resume() => Interlocked.Exchange(ref _tcs, null)?.TrySetResult();

    public Task WaitIfPausedAsync(CancellationToken ct)
    {
        var t = Volatile.Read(ref _tcs);
        return t == null ? Task.CompletedTask : t.Task.WaitAsync(ct); // cancel releases waiters
    }
}

/// <summary>Immutable run configuration. Built by the CLI from argv or the GUI from its controls.</summary>
public sealed class CopyOptions
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required string Destination            { get; init; }
    public CopyOperation Operation     { get; init; } = CopyOperation.Copy;
    public bool          Verify        { get; init; } = true;
    public int           Threads       { get; init; } = Math.Min(4, Environment.ProcessorCount);
    /// <summary>Bytes/sec ceiling; 0 = unlimited.</summary>
    public long          MaxBytesPerSec { get; init; }
    public ConflictMode  Conflict      { get; init; } = ConflictMode.Skip;
    public int           BufferSize    { get; init; } = 1024 * 1024;
    public bool          PreserveTimestamps  { get; init; } = true;
    public bool          PreservePermissions { get; init; } = true;

    /// <summary>Plan only — enumerate, count, detect conflicts; copy nothing, touch nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Glob patterns (`*`/`?`) to exclude. A pattern containing a path separator matches the
    /// path relative to the source root ('/' or '\'); otherwise it matches the entry NAME.
    /// Matching directories are pruned (their whole subtree is never walked). Case-insensitive.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Cooperative pause; null = not pausable (CLI default — Ctrl-Z does it at OS level).</summary>
    public PauseGate? Pause { get; init; }

    /// <summary>
    /// Called when <see cref="Conflict"/> is Ask and a destination exists. Prompts are serialized
    /// (one at a time); ApplyToAll caches the answer for the rest of the run. Null → Ask acts as Skip.
    /// </summary>
    public Func<FileItem, CancellationToken, Task<ConflictDecision>>? ConflictPrompt { get; init; }

    /// <summary>Fires once per item on reaching a terminal state (Done/Skipped/Failed). Exceptions swallowed.</summary>
    public Action<FileItem>? OnItemCompleted { get; init; }
}

/// <summary>Periodic progress snapshot pushed to the CLI.</summary>
public readonly record struct CopyProgress(
    long TotalBytes, long BytesDone,
    int TotalFiles, int FilesDone, int FilesFailed, int FilesSkipped,
    string? CurrentFile, long CurrentFileBytes, long CurrentFileTotal,
    double ElapsedSeconds)
{
    /// <summary>Overall job completion (all files).</summary>
    public double Fraction => TotalBytes > 0 ? (double)BytesDone / TotalBytes : 0;
    /// <summary>Completion of the file currently being worked on.</summary>
    public double CurrentFileFraction => CurrentFileTotal > 0 ? (double)CurrentFileBytes / CurrentFileTotal : 0;
    public double BytesPerSec => ElapsedSeconds > 0 ? BytesDone / ElapsedSeconds : 0;
    /// <summary>Estimated seconds remaining at the current average rate; null until measurable.</summary>
    public double? EtaSeconds =>
        BytesPerSec > 0 && TotalBytes > BytesDone ? (TotalBytes - BytesDone) / BytesPerSec : null;
}

/// <summary>Terminal result of a run.</summary>
public sealed class CopyResult
{
    public int  TotalFiles    { get; set; }
    public int  FilesCopied   { get; set; }
    public int  FilesSkipped  { get; set; }
    public int  FilesFailed   { get; set; }
    /// <summary>Entries pruned by --exclude (files + pruned directories).</summary>
    public int  FilesExcluded { get; set; }
    /// <summary>Symlinks/reparse points seen during enumeration and deliberately not followed.</summary>
    public int  SymlinksSkipped { get; set; }
    /// <summary>MOVE only: emptied source directories removed / left behind (non-empty or locked).</summary>
    public int  SourceDirsRemoved { get; set; }
    public int  SourceDirsKept    { get; set; }
    /// <summary>Dry-run: total bytes the plan would transfer, and how many dests already exist.</summary>
    public long PlannedBytes  { get; set; }
    public int  Conflicts     { get; set; }
    public bool DryRun        { get; set; }
    public long BytesCopied   { get; set; }
    public TimeSpan Elapsed   { get; set; }
    public List<(string Path, string Error)> Errors { get; } = new();
    public bool Success => FilesFailed == 0;
}
