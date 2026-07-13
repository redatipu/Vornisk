using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VorniskCli.Core;

namespace VorniskGui.ViewModels;

/// <summary>One queued transfer shown in the queue list.</summary>
public sealed class QueuedJobViewModel : ObservableObject
{
    public required CopyOptions Options { get; init; }
    public required string Description { get; init; }

    private string _status = "Queued";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsPending => Status == "Queued";
}

/// <summary>Settings persisted between runs (~/.config/vornisk/gui.json on Linux, %APPDATA% on Windows).</summary>
public sealed class GuiSettings
{
    public double Width  { get; set; } = 560;
    public double Height { get; set; } = 640;
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool   IsMove { get; set; }
    public bool   Verify { get; set; } = true;
    public int    ConflictIndex { get; set; } // 0=Ask 1=Skip 2=Overwrite 3=Rename
    public int    Threads { get; set; } = Math.Min(4, Environment.ProcessorCount);
    public string Limit { get; set; } = "";
}

public sealed class MainViewModel : ObservableObject
{
    private readonly CopyEngine _engine = new();
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;
    private bool _draining;

    /// <summary>Set by the view: shows the conflict dialog on the UI thread. Null until the window is up.</summary>
    public Func<FileItem, Task<ConflictDecision>>? ConflictDialogHandler { get; set; }

    public MainViewModel()
    {
        StartCommand       = new AsyncRelayCommand(StartOrQueueAsync, CanStart);
        CancelCommand      = new RelayCommand(Cancel, () => IsRunning);
        PauseResumeCommand = new RelayCommand(TogglePause, () => IsRunning);
        ClearQueueCommand  = new RelayCommand(ClearQueue, () => Jobs.Any(j => j.IsPending));
        OpenDestCommand    = new RelayCommand(OpenDest, () => !string.IsNullOrEmpty(_lastCompletedDest));
        LoadSettings();
    }

    // ── inputs ──────────────────────────────────────────────────────────────────────

    private string _sourcePath = "";
    public string SourcePath { get => _sourcePath; set { if (SetProperty(ref _sourcePath, value)) StartCommand.NotifyCanExecuteChanged(); } }

    private string _destinationPath = "";
    public string DestinationPath { get => _destinationPath; set { if (SetProperty(ref _destinationPath, value)) StartCommand.NotifyCanExecuteChanged(); } }

    private bool _isMove;
    public bool IsMove { get => _isMove; set => SetProperty(ref _isMove, value); }

    private bool _verify = true;
    public bool Verify { get => _verify; set => SetProperty(ref _verify, value); }

    // Index order matters for persistence: 0=Ask 1=Skip 2=Overwrite 3=Rename. Ask is the default —
    // matches the Windows edition's interactive behavior.
    public ObservableCollection<string> ConflictModes { get; } = new() { "Ask", "Skip", "Overwrite", "Rename" };

    private int _conflictIndex;
    public int ConflictIndex { get => _conflictIndex; set => SetProperty(ref _conflictIndex, value); }

    private decimal _threadsValue = Math.Min(4, Environment.ProcessorCount);
    public decimal ThreadsValue { get => _threadsValue; set => SetProperty(ref _threadsValue, value); }

    private string _limitText = "";
    public string LimitText { get => _limitText; set => SetProperty(ref _limitText, value); }

    // ── state ───────────────────────────────────────────────────────────────────────

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                StartCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                PauseResumeCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }
    public bool IsIdle => !IsRunning;
    public string StartButtonText => IsRunning ? "Queue" : "Start";

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PauseButtonText)); }
    }
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    // Per-file bar (TeraCopy-style dual progress): current file on top, whole job below.
    private double _fileProgressValue;
    public double FileProgressValue { get => _fileProgressValue; set => SetProperty(ref _fileProgressValue, value); }

    private string _fileProgressText = "";
    public string FileProgressText { get => _fileProgressText; set => SetProperty(ref _fileProgressText, value); }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set => SetProperty(ref _currentFile, value); }

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _log = "";
    public string Log { get => _log; set => SetProperty(ref _log, value); }

    public ObservableCollection<QueuedJobViewModel> Jobs { get; } = new();
    public bool HasJobs => Jobs.Count > 0;

    private string? _lastCompletedDest;

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand OpenDestCommand { get; }

    // ── actions ─────────────────────────────────────────────────────────────────────

    private bool CanStart() =>
        !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>Start = run now if idle, otherwise append to the queue; one drain loop runs jobs in order.</summary>
    private async Task StartOrQueueAsync()
    {
        long limit;
        try { limit = ParseSize(LimitText); }
        catch (Exception ex) { StatusText = $"Bad limit: {ex.Message}"; return; }

        var mode = ConflictIndex switch { 1 => ConflictMode.Skip, 2 => ConflictMode.Overwrite, 3 => ConflictMode.Rename, _ => ConflictMode.Ask };
        var opt = new CopyOptions
        {
            Sources = new[] { SourcePath.Trim() },
            Destination = DestinationPath.Trim(),
            Operation = IsMove ? CopyOperation.Move : CopyOperation.Copy,
            Verify = Verify,
            Threads = Math.Max(1, (int)ThreadsValue),
            MaxBytesPerSec = limit,
            Conflict = mode,
            ConflictPrompt = mode == ConflictMode.Ask ? PromptConflictAsync : null,
        };

        var job = new QueuedJobViewModel
        {
            Options = opt,
            Description = $"{(IsMove ? "Move" : "Copy")}  {opt.Sources[0]}  →  {opt.Destination}",
        };
        Jobs.Add(job);
        OnPropertyChanged(nameof(HasJobs));
        ClearQueueCommand.NotifyCanExecuteChanged();

        if (!_draining)
            await DrainQueueAsync();
    }

    private async Task DrainQueueAsync()
    {
        _draining = true;
        IsRunning = true;
        try
        {
            while (true)
            {
                var job = Jobs.FirstOrDefault(j => j.IsPending);
                if (job == null) break;
                await RunJobAsync(job);
            }
        }
        finally
        {
            _draining = false;
            IsRunning = false;
            IsPaused  = false;
            CurrentFile = "";
            OpenDestCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunJobAsync(QueuedJobViewModel job)
    {
        job.Status = "Running";
        ProgressValue = 0; FileProgressValue = 0;
        ProgressText = ""; FileProgressText = ""; CurrentFile = "";
        StatusText = job.Options.Operation == CopyOperation.Move ? "Moving…" : "Copying…";
        AppendLog($"--- {job.Description} ---");

        _gate = new PauseGate();
        _cts  = new CancellationTokenSource();
        IsPaused = false;
        var progress = new Progress<CopyProgress>(OnProgress); // captures UI SynchronizationContext
        var opt = job.Options;

        try
        {
            // CopyOptions is init-only; rebuild with the live per-job pause gate.
            var run = new CopyOptions
            {
                Sources = opt.Sources, Destination = opt.Destination, Operation = opt.Operation,
                Verify = opt.Verify, Threads = opt.Threads, MaxBytesPerSec = opt.MaxBytesPerSec,
                Conflict = opt.Conflict, ConflictPrompt = opt.ConflictPrompt, Pause = _gate,
            };
            var result = await Task.Run(() => _engine.RunAsync(run, progress, _cts.Token)).ConfigureAwait(true);

            ProgressValue = 100;
            job.Status = result.Success ? "Done" : $"Failed ({result.FilesFailed})";
            StatusText = result.Success
                ? $"Done — {result.FilesCopied} file(s), {Human(result.BytesCopied)} in {result.Elapsed.TotalSeconds:0.0}s"
                : $"Completed with {result.FilesFailed} failure(s)";
            if (result.Success) _lastCompletedDest = run.Destination;

            AppendLog($"Copied {result.FilesCopied}/{result.TotalFiles}, skipped {result.FilesSkipped}, failed {result.FilesFailed} ({Human(result.BytesCopied)}, {result.Elapsed.TotalSeconds:0.0}s)");
            if (result.SymlinksSkipped > 0) AppendLog($"  {result.SymlinksSkipped} symlink(s) skipped (never followed)");
            if (result.SourceDirsRemoved > 0 || result.SourceDirsKept > 0)
                AppendLog($"  source dirs: {result.SourceDirsRemoved} removed" + (result.SourceDirsKept > 0 ? $", {result.SourceDirsKept} kept (not empty)" : ""));
            // Cap the error dump: `Log +=` per line is quadratic — a mass failure with 100k errors
            // would freeze the UI building strings.
            const int maxLoggedErrors = 100;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < result.Errors.Count && i < maxLoggedErrors; i++)
                sb.AppendLine($"  ! {result.Errors[i].Path}: {result.Errors[i].Error}");
            if (result.Errors.Count > maxLoggedErrors)
                sb.AppendLine($"  … and {result.Errors.Count - maxLoggedErrors:N0} more failures (run the CLI with --json for the full list)");
            if (sb.Length > 0) AppendLog(sb.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            job.Status = "Cancelled";
            StatusText = "Cancelled.";
            AppendLog("--- cancelled ---");
        }
        catch (Exception ex)
        {
            job.Status = "Error";
            StatusText = $"Error: {ex.Message}";
            AppendLog($"--- error: {ex.Message} ---");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _gate = null;
        }
    }

    private Task<ConflictDecision> PromptConflictAsync(FileItem item, CancellationToken ct)
    {
        var handler = ConflictDialogHandler;
        // No dialog wired (headless/tests) → safe default, same as the engine's own fallback.
        return handler == null
            ? Task.FromResult(new ConflictDecision(ConflictMode.Skip, false))
            : handler(item);
    }

    private void Cancel()
    {
        StatusText = "Cancelling…";
        _gate?.Resume(); // a paused job must observe the cancel
        IsPaused = false;
        _cts?.Cancel();
    }

    private void TogglePause()
    {
        var g = _gate;
        if (g == null) return;
        if (g.IsPaused) { g.Resume(); IsPaused = false; StatusText = "Resumed."; }
        else            { g.Pause();  IsPaused = true;  StatusText = "Paused."; }
    }

    private void ClearQueue()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
            if (Jobs[i].IsPending) Jobs.RemoveAt(i);
        OnPropertyChanged(nameof(HasJobs));
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    private void OpenDest()
    {
        var d = _lastCompletedDest;
        if (string.IsNullOrEmpty(d)) return;
        var dir = Directory.Exists(d) ? d : Path.GetDirectoryName(d);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", dir);
            else
                Process.Start("xdg-open", dir);
        }
        catch (Exception ex) { StatusText = $"Could not open folder: {ex.Message}"; }
    }

    private void OnProgress(CopyProgress p)
    {
        ProgressValue = p.Fraction * 100;
        ProgressText = $"{p.Fraction * 100:0.0}%   {Human(p.BytesDone)} / {Human(p.TotalBytes)}   " +
                       $"{Human((long)p.BytesPerSec)}/s   ETA {FmtEta(p.EtaSeconds)}   files {p.FilesDone}/{p.TotalFiles}" +
                       (p.FilesFailed > 0 ? $"   failed {p.FilesFailed}" : "") +
                       (p.FilesSkipped > 0 ? $"   skipped {p.FilesSkipped}" : "");
        CurrentFile = p.CurrentFile ?? "";

        // Per-file bar — same data the CLI's top bar uses.
        FileProgressValue = p.CurrentFileFraction * 100;
        FileProgressText  = p.CurrentFileTotal > 0
            ? $"{p.CurrentFileFraction * 100:0.0}%   {Human(p.CurrentFileBytes)} / {Human(p.CurrentFileTotal)}"
            : "";
    }

    private void AppendLog(string line) => Log += (Log.Length == 0 ? "" : "\n") + line;

    // ── settings persistence ─────────────────────────────────────────────────────────

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vornisk", "gui.json");

    /// <summary>Window size persisted by the view; everything else by the VM.</summary>
    public GuiSettings Settings { get; private set; } = new();

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new GuiSettings();
        }
        catch { Settings = new GuiSettings(); /* corrupt settings must never block launch */ }

        SourcePath      = Settings.Source;
        DestinationPath = Settings.Destination;
        IsMove          = Settings.IsMove;
        Verify          = Settings.Verify;
        ConflictIndex   = Math.Clamp(Settings.ConflictIndex, 0, 3);
        ThreadsValue    = Math.Clamp(Settings.Threads, 1, 64);
        LimitText       = Settings.Limit;
    }

    public void SaveSettings(double width, double height)
    {
        try
        {
            Settings.Width  = width;
            Settings.Height = height;
            Settings.Source = SourcePath;
            Settings.Destination = DestinationPath;
            Settings.IsMove = IsMove;
            Settings.Verify = Verify;
            Settings.ConflictIndex = ConflictIndex;
            Settings.Threads = Math.Max(1, (int)ThreadsValue);
            Settings.Limit  = LimitText;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort — closing must never fail on a read-only home */ }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    internal static long ParseSize(string? s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0) return 0; // empty = unlimited
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

    private static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {u[i]}";
    }

    private static string FmtEta(double? seconds)
    {
        if (seconds is not { } s || s < 0 || s > 30 * 24 * 3600) return "--:--";
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // Used by the View's Browse buttons (code-behind owns the StorageProvider).
    public void SetSource(string path) => SourcePath = path;
    public void SetDestination(string path) => DestinationPath = path;
}
