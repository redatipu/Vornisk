using System.Globalization;
using VorniskCli.Core;

return await CliRunner.Run(args);

internal static class CliRunner
{
    private const string Version = "0.2.0";

    private sealed class CliFlags
    {
        public bool Quiet, Json, JsonProgress, Verbose;
        /// <summary>Bars only on a plain interactive run — any structured/verbose mode owns the output.</summary>
        public bool Bars => !Quiet && !Json && !JsonProgress && !Verbose;
    }

    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }
        // 0.2.0: -v moved to --verbose (common CLI convention); version is -V/--version.
        if (args.Contains("-V") || args.Contains("--version"))
        {
            Console.WriteLine($"vornisk {Version}");
            return 0;
        }

        CopyOptions opt;
        CliFlags flags;
        try
        {
            (opt, flags) = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"vornisk: {ex.Message}");
            Console.Error.WriteLine("Try 'vornisk --help'.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var engine = new CopyEngine();
        IProgress<CopyProgress>? progress =
            flags.JsonProgress ? new Progress<CopyProgress>(RenderJsonProgress)
            : flags.Bars       ? new Progress<CopyProgress>(RenderProgress)
            : null;

        bool endBarLine = flags.Bars && !_ansi; // plain single-line mode needs its \r line terminated

        try
        {
            var result = await engine.RunAsync(opt, progress, cts.Token);
            if (endBarLine) Console.Error.WriteLine();

            if (result.DryRun)
            {
                if (flags.Json || flags.JsonProgress) PrintJson(result);
                else if (!flags.Quiet) PrintDryRunSummary(result);
            }
            else if (flags.Json || flags.JsonProgress) PrintJson(result);
            else if (!flags.Quiet) PrintSummary(result);

            return result.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            if (endBarLine) Console.Error.WriteLine();
            Console.Error.WriteLine("vornisk: cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // Backstop: anything that escapes the engine (bad/blocked destination, enumeration I/O error
            // on a flaky mount, path too long, …) becomes a clean message + exit 1 — never a crash dump.
            if (endBarLine) Console.Error.WriteLine();
            Console.Error.WriteLine($"vornisk: {ex.Message}");
            return 1;
        }
    }

    // ── argument parsing ────────────────────────────────────────────────────────────

    private static (CopyOptions opt, CliFlags flags) ParseArgs(string[] args)
    {
        var op       = CopyOperation.Copy;
        bool verify  = true;
        int threads  = Math.Min(4, Environment.ProcessorCount);
        long limit   = 0;
        var conflict = ConflictMode.Skip;
        int buffer   = 1024 * 1024;
        bool preserve = true;
        bool dryRun  = false;
        var excludes = new List<string>();
        var flags    = new CliFlags();
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-m": case "--move":        op = CopyOperation.Move; break;
                case "--no-verify":              verify = false; break;
                case "--overwrite":              conflict = ConflictMode.Overwrite; break;
                case "--no-preserve":            preserve = false; break;
                case "-q": case "--quiet":       flags.Quiet = true; break;
                case "--json":                   flags.Json = true; break;
                case "--json-progress":          flags.JsonProgress = true; break;
                case "-v": case "--verbose":     flags.Verbose = true; break;
                case "-n": case "--dry-run":     dryRun = true; break;
                case "-x": case "--exclude":     excludes.Add(NextValue(args, ref i, a)); break;
                case "-j": case "--threads":     threads = ParseInt(NextValue(args, ref i, a), a); break;
                case "-l": case "--limit":       limit = ParseSize(NextValue(args, ref i, a)); break;
                case "--buffer":                 buffer = (int)Math.Min(ParseSize(NextValue(args, ref i, a)), int.MaxValue); break;
                case "--on-conflict":            conflict = ParseConflict(NextValue(args, ref i, a)); break;
                default:
                    if (a.StartsWith('-') && a.Length > 1)
                        throw new ArgumentException($"unknown option '{a}'");
                    positionals.Add(a);
                    break;
            }
        }

        if (positionals.Count < 2)
            throw new ArgumentException("need at least one SOURCE and a DESTINATION");
        if (threads < 1) throw new ArgumentException("--threads must be >= 1");
        if (buffer < 4096) throw new ArgumentException("--buffer must be >= 4096");

        var dest    = positionals[^1];
        var sources = positionals.GetRange(0, positionals.Count - 1);

        var opt = new CopyOptions
        {
            Sources = sources,
            Destination = dest,
            Operation = op,
            Verify = verify,
            Threads = threads,
            MaxBytesPerSec = limit,
            Conflict = conflict,
            BufferSize = buffer,
            PreserveTimestamps = preserve,
            PreservePermissions = preserve,
            DryRun = dryRun,
            Excludes = excludes,
            // -v: one line per file on stderr (dry-run: the planned transfer). Bars are off in
            // verbose mode, so the lines never fight the ANSI cursor moves.
            OnItemCompleted = flags.Verbose ? PrintItemLine : null,
        };
        return (opt, flags);
    }

    private static void PrintItemLine(FileItem item)
    {
        var line = item.Status switch
        {
            ItemStatus.Failed  => $"  FAIL  {item.SourcePath}: {item.Error}",
            ItemStatus.Skipped => $"  skip  {item.SourcePath}",
            ItemStatus.Pending => $"  plan  {item.SourcePath} -> {item.DestPath} ({Human(item.SizeBytes)})",
            _                  => $"  ok    {item.SourcePath} -> {item.DestPath} ({Human(item.SizeBytes)})",
        };
        Console.Error.WriteLine(line);
    }

    private static string NextValue(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"option '{opt}' needs a value");
        return args[++i];
    }

    private static int ParseInt(string s, string opt) =>
        int.TryParse(s, out var n) ? n : throw new ArgumentException($"'{opt}' expects an integer, got '{s}'");

    private static ConflictMode ParseConflict(string s) => s.ToLowerInvariant() switch
    {
        "skip" => ConflictMode.Skip,
        "overwrite" => ConflictMode.Overwrite,
        "rename" => ConflictMode.Rename,
        _ => throw new ArgumentException($"--on-conflict expects skip|overwrite|rename, got '{s}'"),
    };

    /// <summary>Parse "200M", "1G", "512K", or a bare byte count. Suffixes are base-1024.</summary>
    internal static long ParseSize(string s)
    {
        s = s.Trim();
        if (s.Length == 0) throw new ArgumentException("empty size value");
        long mult = 1;
        char last = char.ToUpperInvariant(s[^1]);
        if (last is 'K' or 'M' or 'G' or 'T')
        {
            mult = last switch { 'K' => 1024L, 'M' => 1024L * 1024, 'G' => 1024L * 1024 * 1024, _ => 1024L * 1024 * 1024 * 1024 };
            s = s[..^1];
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
            throw new ArgumentException($"invalid size '{s}'");
        return (long)(v * mult);
    }

    // ── output ──────────────────────────────────────────────────────────────────────

    // A real terminal gets the two-line live display (ANSI cursor moves). A redirected/piped
    // stderr (logs, CI) gets a plain single overwriting line — no escape codes to pollute the file.
    private static readonly bool _ansi = !Console.IsErrorRedirected;
    private static bool _barsShown;

    /// <summary>
    /// Two stacked progress bars: line 1 = the file currently being worked on, line 2 = the whole job.
    /// Redraw works by moving the cursor up two lines each tick and rewriting both.
    /// </summary>
    private static void RenderProgress(CopyProgress p)
    {
        if (!_ansi) { RenderProgressPlain(p); return; }

        int width = SafeWidth();
        int barW  = Math.Clamp(width / 3, 10, 32);

        string name = string.IsNullOrEmpty(p.CurrentFile) ? "" : Path.GetFileName(p.CurrentFile);
        string l1 = $"  file   {Bar(p.CurrentFileFraction, barW)} {p.CurrentFileFraction * 100,5:0.0}%  " +
                    $"{Human(p.CurrentFileBytes)}/{Human(p.CurrentFileTotal)}  {name}";
        string l2 = $"  total  {Bar(p.Fraction, barW)} {p.Fraction * 100,5:0.0}%  " +
                    $"{Human(p.BytesDone)}/{Human(p.TotalBytes)}  {Human((long)p.BytesPerSec)}/s  " +
                    $"ETA {FmtEta(p.EtaSeconds)}  files {p.FilesDone}/{p.TotalFiles}" +
                    (p.FilesFailed > 0 ? $"  failed {p.FilesFailed}" : "") +
                    (p.FilesSkipped > 0 ? $"  skipped {p.FilesSkipped}" : "");

        var sb = new System.Text.StringBuilder();
        if (_barsShown) sb.Append("\x1b[2A");                      // back up over the two bar lines
        sb.Append('\r').Append("\x1b[K").Append(Fit(l1, width)).Append('\n');  // \x1b[K clears to EOL
        sb.Append('\r').Append("\x1b[K").Append(Fit(l2, width)).Append('\n');
        Console.Error.Write(sb.ToString());
        _barsShown = true;
    }

    private static void RenderProgressPlain(CopyProgress p)
    {
        var line = $"\r{p.Fraction * 100,5:0.0}%  {Human(p.BytesDone)}/{Human(p.TotalBytes)}  " +
                   $"{Human((long)p.BytesPerSec)}/s  ETA {FmtEta(p.EtaSeconds)}  files {p.FilesDone}/{p.TotalFiles}" +
                   (p.FilesFailed > 0 ? $"  failed {p.FilesFailed}" : "") +
                   (p.FilesSkipped > 0 ? $"  skipped {p.FilesSkipped}" : "");
        if (line.Length < 79) line += new string(' ', 79 - line.Length); // clear leftovers
        Console.Error.Write(line);
    }

    private static string FmtEta(double? seconds)
    {
        if (seconds is not { } s || s < 0 || s > 30 * 24 * 3600) return "--:--";
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ── --json-progress: one NDJSON event per tick on stdout (final summary object follows) ──
    private static readonly object _jsonLock = new(); // Progress<T> posts to the pool — keep lines whole

    private static void RenderJsonProgress(CopyProgress p)
    {
        var eta = p.EtaSeconds is { } e ? e.ToString("0.0", CultureInfo.InvariantCulture) : "null";
        var line =
            $"{{\"type\":\"progress\",\"bytesDone\":{p.BytesDone},\"totalBytes\":{p.TotalBytes}," +
            $"\"filesDone\":{p.FilesDone},\"totalFiles\":{p.TotalFiles},\"failed\":{p.FilesFailed}," +
            $"\"skipped\":{p.FilesSkipped},\"bytesPerSec\":{(long)p.BytesPerSec},\"etaSeconds\":{eta}," +
            $"\"currentFile\":{Quote(p.CurrentFile ?? "")}}}";
        lock (_jsonLock) Console.WriteLine(line);
    }

    private static string Bar(double frac, int width)
    {
        frac = Math.Clamp(frac, 0, 1);
        int fill = (int)Math.Round(frac * width);
        return "[" + new string('#', fill) + new string('-', width - fill) + "]";
    }

    /// <summary>Truncate to the terminal width so the line never wraps (which would break the cursor math).</summary>
    private static string Fit(string s, int width)
    {
        if (width <= 1 || s.Length < width) return s;
        return string.Concat(s.AsSpan(0, width - 2), "…");
    }

    private static int SafeWidth()
    {
        try { int w = Console.WindowWidth; return w > 0 ? w : 80; }
        catch { return 80; }
    }

    private static void PrintDryRunSummary(CopyResult r)
    {
        Console.WriteLine($"DRY RUN — nothing copied. Plan: {r.TotalFiles} file(s), {Human(r.PlannedBytes)}." +
            (r.Conflicts > 0 ? $"  {r.Conflicts} destination conflict(s)." : "") +
            (r.FilesExcluded > 0 ? $"  {r.FilesExcluded} excluded." : "") +
            (r.SymlinksSkipped > 0 ? $"  {r.SymlinksSkipped} symlink(s) skipped (not followed)." : ""));
    }

    private static void PrintSummary(CopyResult r)
    {
        Console.WriteLine($"Copied {r.FilesCopied}/{r.TotalFiles} files ({Human(r.BytesCopied)}) in {r.Elapsed.TotalSeconds:0.0}s" +
            (r.Elapsed.TotalSeconds > 0 ? $" @ {Human((long)(r.BytesCopied / r.Elapsed.TotalSeconds))}/s" : ""));
        if (r.FilesSkipped > 0) Console.WriteLine($"Skipped: {r.FilesSkipped}");
        if (r.FilesExcluded > 0) Console.WriteLine($"Excluded: {r.FilesExcluded}");
        if (r.SymlinksSkipped > 0) Console.WriteLine($"Symlinks: {r.SymlinksSkipped} skipped (never followed — copy them explicitly if intended)");
        if (r.SourceDirsRemoved > 0 || r.SourceDirsKept > 0)
            Console.WriteLine($"Source dirs: {r.SourceDirsRemoved} removed" +
                (r.SourceDirsKept > 0 ? $", {r.SourceDirsKept} kept (not empty)" : ""));
        if (r.FilesFailed > 0)
        {
            Console.WriteLine($"Failed:  {r.FilesFailed}");
            // Cap the dump — a mass failure on a huge tree shouldn't flood the terminal with
            // 100k lines. --json carries the complete error list for scripts.
            const int maxShown = 100;
            int shown = 0;
            foreach (var (path, err) in r.Errors)
            {
                if (shown++ == maxShown)
                {
                    Console.Error.WriteLine($"  … and {r.Errors.Count - maxShown:N0} more failures (use --json for the full list)");
                    break;
                }
                Console.Error.WriteLine($"  ! {path}: {err}");
            }
        }
    }

    private static void PrintJson(CopyResult r)
    {
        var errs = string.Join(",", r.Errors.Select(e =>
            $"{{\"path\":{Quote(e.Path)},\"error\":{Quote(e.Error)}}}"));
        var line =
            $"{{\"type\":\"summary\",\"totalFiles\":{r.TotalFiles},\"copied\":{r.FilesCopied},\"skipped\":{r.FilesSkipped}," +
            $"\"failed\":{r.FilesFailed},\"excluded\":{r.FilesExcluded},\"symlinksSkipped\":{r.SymlinksSkipped}," +
            $"\"sourceDirsRemoved\":{r.SourceDirsRemoved},\"sourceDirsKept\":{r.SourceDirsKept}," +
            $"\"dryRun\":{(r.DryRun ? "true" : "false")},\"plannedBytes\":{r.PlannedBytes},\"conflicts\":{r.Conflicts}," +
            $"\"bytes\":{r.BytesCopied},\"seconds\":{r.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}," +
            $"\"success\":{(r.Success ? "true" : "false")},\"errors\":[{errs}]}}";
        lock (_jsonLock) Console.WriteLine(line);
    }

    private static string Quote(string s)
    {
        var b = new System.Text.StringBuilder("\"");
        foreach (var c in s)
        {
            // SEC: escape ALL control chars (< 0x20), not just \n\r\t — a filename with a raw control
            // byte (or embedded quote/backslash) would otherwise emit malformed/injectable JSON.
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c == '\t') b.Append("\\t");
            else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    internal static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {u[i]}";
    }

    /// <summary>
    /// ASCII banner (pure ASCII on purpose — renders on any terminal/locale, no Unicode box chars).
    /// Shown on help / bare invocation only: --version stays one-line parseable, --json/--quiet and
    /// normal runs stay banner-free so scripts and pipes never see it.
    /// </summary>
    private static void PrintBanner()
    {
        Console.WriteLine(
@"__     __ ___   ____   _   _  ___  ____   _  __
\ \   / // _ \ |  _ \ | \ | ||_ _|/ ___| | |/ /
 \ \ / /| | | || |_) ||  \| | | | \___ \ | ' /
  \ V / | |_| ||  _ < | |\  | | |  ___) || . \
   \_/   \___/ |_| \_\|_| \_||___||____/ |_|\_\
");
    }

    private static void PrintHelp()
    {
        PrintBanner();
        Console.WriteLine(
@"vornisk — fast, verified file copy/move (cross-platform CLI)

USAGE:
    vornisk [OPTIONS] SOURCE... DESTINATION

ARGS:
    SOURCE...       one or more files or directories
    DESTINATION     target file (single source) or directory (multiple / dir sources)

OPTIONS:
    -m, --move              move instead of copy (delete source after verified write;
                            emptied source dirs are removed, non-empty ones kept)
        --no-verify         skip the xxHash3 verify pass (faster, less safe)
    -n, --dry-run           plan only: show/count what WOULD transfer, touch nothing
    -x, --exclude GLOB      exclude entries (repeatable); '*'/'?' globs match the entry
                            name, or the source-relative path if the pattern has a '/'
    -j, --threads N         concurrent file workers (default: min(4, CPUs))
    -l, --limit RATE        throttle, e.g. 200M, 1G, 512K (base-1024 bytes/sec; default: unlimited)
        --on-conflict MODE  skip | overwrite | rename   (default: skip)
        --overwrite         shorthand for --on-conflict overwrite
        --buffer SIZE       I/O buffer size (default: 1M)
        --no-preserve       do not preserve timestamps / Unix permissions
    -v, --verbose           one line per file on stderr (disables the progress bars)
    -q, --quiet             no progress, no summary
        --json              machine-readable summary on stdout
        --json-progress     NDJSON progress events on stdout (one per tick), summary last
    -h, --help              show this help
    -V, --version           show version

    Pause/resume: Ctrl-Z suspends, `fg` resumes (standard job control).
    Symlinks found while walking a tree are never followed; the summary counts them.

EXIT CODES:
    0 success   1 one or more files failed   2 usage error   130 cancelled

EXAMPLES:
    vornisk /data/file.iso /backup/
    vornisk -m --on-conflict rename /src/a /src/b /dst
    vornisk -n -x '*.tmp' -x node_modules /repo /backup     # rehearse first
    sudo vornisk -j 8 -l 300M /mnt/array /mnt/nas/backup");
    }
}
