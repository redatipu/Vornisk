namespace VorniskCli.Core;

public enum CopyOperation { Copy, Move }

public enum ConflictMode { Skip, Overwrite, Rename }

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

/// <summary>Immutable run configuration. Built by the CLI from argv.</summary>
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
}

/// <summary>Terminal result of a run.</summary>
public sealed class CopyResult
{
    public int  TotalFiles    { get; set; }
    public int  FilesCopied   { get; set; }
    public int  FilesSkipped  { get; set; }
    public int  FilesFailed   { get; set; }
    public long BytesCopied   { get; set; }
    public TimeSpan Elapsed   { get; set; }
    public List<(string Path, string Error)> Errors { get; } = new();
    public bool Success => FilesFailed == 0;
}
