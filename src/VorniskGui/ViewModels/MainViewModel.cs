using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VorniskCli.Core;

namespace VorniskGui.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CopyEngine _engine = new();
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        StartCommand  = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
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

    public ObservableCollection<string> ConflictModes { get; } = new() { "Skip", "Overwrite", "Rename" };

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
            }
        }
    }
    public bool IsIdle => !IsRunning;

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set => SetProperty(ref _currentFile, value); }

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _log = "";
    public string Log { get => _log; set => SetProperty(ref _log, value); }

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ── actions ─────────────────────────────────────────────────────────────────────

    private bool CanStart() =>
        !IsRunning && !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);

    private async Task StartAsync()
    {
        long limit;
        try { limit = ParseSize(LimitText); }
        catch (Exception ex) { StatusText = $"Bad limit: {ex.Message}"; return; }

        var opt = new CopyOptions
        {
            Sources = new[] { SourcePath.Trim() },
            Destination = DestinationPath.Trim(),
            Operation = IsMove ? CopyOperation.Move : CopyOperation.Copy,
            Verify = Verify,
            Threads = Math.Max(1, (int)ThreadsValue),
            MaxBytesPerSec = limit,
            Conflict = (ConflictMode)Math.Clamp(ConflictIndex, 0, 2),
        };

        IsRunning = true;
        ProgressValue = 0;
        ProgressText = "";
        CurrentFile = "";
        StatusText = IsMove ? "Moving…" : "Copying…";
        AppendLog($"--- {(IsMove ? "Move" : "Copy")} started: {opt.Sources[0]} → {opt.Destination} ---");

        _cts = new CancellationTokenSource();
        var progress = new Progress<CopyProgress>(OnProgress); // captures UI SynchronizationContext

        try
        {
            var result = await Task.Run(() => _engine.RunAsync(opt, progress, _cts.Token)).ConfigureAwait(true);

            ProgressValue = 100;
            StatusText = result.Success
                ? $"Done — {result.FilesCopied} file(s), {Human(result.BytesCopied)} in {result.Elapsed.TotalSeconds:0.0}s"
                : $"Completed with {result.FilesFailed} failure(s)";
            AppendLog($"Copied {result.FilesCopied}/{result.TotalFiles}, skipped {result.FilesSkipped}, failed {result.FilesFailed} ({Human(result.BytesCopied)}, {result.Elapsed.TotalSeconds:0.0}s)");
            foreach (var (path, err) in result.Errors)
                AppendLog($"  ! {path}: {err}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            AppendLog("--- cancelled ---");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            AppendLog($"--- error: {ex.Message} ---");
        }
        finally
        {
            IsRunning = false;
            CurrentFile = "";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        StatusText = "Cancelling…";
        _cts?.Cancel();
    }

    private void OnProgress(CopyProgress p)
    {
        ProgressValue = p.Fraction * 100;
        ProgressText = $"{p.Fraction * 100:0.0}%   {Human(p.BytesDone)} / {Human(p.TotalBytes)}   " +
                       $"{Human((long)p.BytesPerSec)}/s   files {p.FilesDone}/{p.TotalFiles}" +
                       (p.FilesFailed > 0 ? $"   failed {p.FilesFailed}" : "") +
                       (p.FilesSkipped > 0 ? $"   skipped {p.FilesSkipped}" : "");
        CurrentFile = p.CurrentFile ?? "";
    }

    private void AppendLog(string line) => Log += (Log.Length == 0 ? "" : "\n") + line;

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

    // Used by the View's Browse buttons (code-behind owns the StorageProvider).
    public void SetSource(string path) => SourcePath = path;
    public void SetDestination(string path) => DestinationPath = path;
}
