using VorniskCli.Core;

namespace VorniskCli.Tests;

public sealed class CopyEngineTests : IDisposable
{
    private readonly string _root;

    public CopyEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vcli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] Rand(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Captures progress reports synchronously (engine reports inline) for deterministic assertions.</summary>
    private sealed class SyncProgress : IProgress<CopyProgress>
    {
        private readonly List<CopyProgress> _items = new();
        public void Report(CopyProgress value) { lock (_items) _items.Add(value); }
        public List<CopyProgress> Snapshot() { lock (_items) return _items.ToList(); }
    }

    private static CopyOptions Opt(IReadOnlyList<string> src, string dst, CopyOperation op = CopyOperation.Copy,
        bool verify = true, ConflictMode conflict = ConflictMode.Skip, long limit = 0)
        => new()
        {
            Sources = src, Destination = dst, Operation = op, Verify = verify,
            Conflict = conflict, MaxBytesPerSec = limit, Threads = 4,
        };

    [Fact]
    public async Task Copy_SingleFile_ContentMatches_AndVerifies()
    {
        var src = Dir("s"); var dst = Dir("d");
        var data = Rand(2 * 1024 * 1024 + 777, 1);
        var f = Path.Combine(src, "a.bin");
        File.WriteAllBytes(f, data);

        var r = await new CopyEngine().RunAsync(Opt(new[] { f }, dst), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(1, r.FilesCopied);
        var outFile = Path.Combine(dst, "a.bin");
        Assert.True(File.Exists(outFile));
        Assert.Equal(data, File.ReadAllBytes(outFile));
        Assert.True(File.Exists(f)); // copy leaves source
    }

    [Fact]
    public async Task Copy_DirectoryTree_StructurePreserved()
    {
        var src = Dir("tree"); var dst = Dir("out");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "root.bin"), Rand(1000, 2));
        File.WriteAllBytes(Path.Combine(src, "sub", "leaf.bin"), Rand(2000, 3));
        Directory.CreateDirectory(Path.Combine(src, "empty")); // empty dir must survive

        var r = await new CopyEngine().RunAsync(Opt(new[] { src }, dst), null, CancellationToken.None);

        Assert.True(r.Success);
        var baseDir = Path.Combine(dst, "tree");
        Assert.True(File.Exists(Path.Combine(baseDir, "root.bin")));
        Assert.True(File.Exists(Path.Combine(baseDir, "sub", "leaf.bin")));
        Assert.True(Directory.Exists(Path.Combine(baseDir, "empty")));
    }

    [Fact]
    public async Task Move_SameDir_DeletesSource()
    {
        var src = Dir("ms"); var dst = Dir("md");
        var data = Rand(64 * 1024, 4);
        var f = Path.Combine(src, "m.bin");
        File.WriteAllBytes(f, data);

        var r = await new CopyEngine().RunAsync(Opt(new[] { f }, dst, op: CopyOperation.Move), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.False(File.Exists(f), "source must be gone after move");
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dst, "m.bin")));
    }

    [Fact]
    public async Task Conflict_Skip_LeavesExistingUntouched()
    {
        var src = Dir("cs"); var dst = Dir("cd");
        File.WriteAllBytes(Path.Combine(src, "x.bin"), Rand(500, 5));
        File.WriteAllText(Path.Combine(dst, "x.bin"), "ORIGINAL");

        var r = await new CopyEngine().RunAsync(Opt(new[] { Path.Combine(src, "x.bin") }, dst, conflict: ConflictMode.Skip), null, CancellationToken.None);

        Assert.Equal(1, r.FilesSkipped);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(dst, "x.bin")));
    }

    [Fact]
    public async Task Conflict_Overwrite_ReplacesContent()
    {
        var src = Dir("os"); var dst = Dir("od");
        var data = Rand(800, 6);
        File.WriteAllBytes(Path.Combine(src, "x.bin"), data);
        File.WriteAllText(Path.Combine(dst, "x.bin"), "OLD");

        var r = await new CopyEngine().RunAsync(Opt(new[] { Path.Combine(src, "x.bin") }, dst, conflict: ConflictMode.Overwrite), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dst, "x.bin")));
    }

    [Fact]
    public async Task Conflict_Rename_CreatesNumberedCopy()
    {
        var src = Dir("rs"); var dst = Dir("rd");
        var data = Rand(900, 7);
        File.WriteAllBytes(Path.Combine(src, "x.bin"), data);
        File.WriteAllText(Path.Combine(dst, "x.bin"), "KEEP");

        var r = await new CopyEngine().RunAsync(Opt(new[] { Path.Combine(src, "x.bin") }, dst, conflict: ConflictMode.Rename), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(dst, "x.bin")));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dst, "x (1).bin")));
    }

    [Fact]
    public async Task MissingSource_ReportedFailed_NotSilent()
    {
        var dst = Dir("ghd");
        var ghost = Path.Combine(_root, "nope.bin");

        var r = await new CopyEngine().RunAsync(Opt(new[] { ghost }, dst), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Equal(1, r.FilesFailed);
        Assert.Single(r.Errors);
    }

    [Fact]
    public async Task Security_DoesNotFollowSymlinkedDirectories()
    {
        var src = Dir("ln_s"); var outside = Dir("ln_outside"); var dst = Dir("ln_d");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET");
        File.WriteAllBytes(Path.Combine(src, "real.bin"), Rand(100, 9));

        var link = Path.Combine(src, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch { return; } // unprivileged (e.g. Windows without Developer Mode) → inconclusive, skip

        var r = await new CopyEngine().RunAsync(Opt(new[] { src }, dst), null, CancellationToken.None);

        Assert.True(r.Success);
        var baseDir = Path.Combine(dst, "ln_s");
        Assert.True(File.Exists(Path.Combine(baseDir, "real.bin")));
        // The symlinked dir must NOT be traversed — the secret behind it must not be copied.
        Assert.False(File.Exists(Path.Combine(baseDir, "link", "secret.txt")),
            "symlinked directory was followed — path-escape security regression");
    }

    [Fact]
    public async Task Progress_ReportsPerFileBytesAndTotal()
    {
        var src = Dir("ps"); var dst = Dir("pd");
        var data = Rand(2 * 1024 * 1024, 11);          // 2 MB so several throttled ticks fire
        var f = Path.Combine(src, "big.bin");
        File.WriteAllBytes(f, data);

        // Synchronous collector: the engine calls IProgress.Report inline, so every tick (incl. the
        // final one) is captured deterministically. Progress<T> would deliver async and race the await.
        var progress = new SyncProgress();

        // single thread → the "current file" slot is unambiguous; 4 MB/s → ~0.5s, multiple 200ms ticks
        var opt = new CopyOptions
        {
            Sources = new[] { f }, Destination = dst, Threads = 1,
            MaxBytesPerSec = 4L * 1024 * 1024,
        };
        var r = await new CopyEngine().RunAsync(opt, progress, CancellationToken.None);
        Assert.True(r.Success);

        var seen = progress.Snapshot();

        // per-file total is reported and equals the file size
        Assert.Contains(seen, p => p.CurrentFileTotal == data.Length);
        // per-file byte counter advances and never exceeds the file total
        Assert.Contains(seen, p => p.CurrentFileBytes > 0);
        Assert.All(seen, p => Assert.True(p.CurrentFileBytes <= p.CurrentFileTotal || p.CurrentFileTotal == 0));
        // current-file fraction stays in range; the file name is surfaced
        Assert.All(seen, p => Assert.InRange(p.CurrentFileFraction, 0.0, 1.0));
        Assert.Contains(seen, p => p.CurrentFile != null && Path.GetFileName(p.CurrentFile) == "big.bin");
    }

    [Fact]
    public async Task BlockedDestination_ReportsFailed_DoesNotThrow()
    {
        // dest tree can't be created because a *file* sits where a directory must go.
        // Regression: this used to throw out of RunAsync and crash the CLI process.
        var src = Dir("bd_src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), Rand(256, 21));
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");

        var dest = Path.Combine(blocker, "target"); // path goes *through* the file → CreateDirectory fails
        var r = await new CopyEngine().RunAsync(Opt(new[] { src }, dest), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.True(r.FilesFailed >= 1);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public async Task RateLimit_StillCopiesCorrectly()
    {
        var src = Dir("ls"); var dst = Dir("ld");
        var data = Rand(1024 * 1024, 8);
        var f = Path.Combine(src, "l.bin");
        File.WriteAllBytes(f, data);

        // 8 MB/s ceiling — 1 MB should take ~0.1s and copy byte-perfectly.
        var r = await new CopyEngine().RunAsync(Opt(new[] { f }, dst, limit: 8L * 1024 * 1024), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dst, "l.bin")));
    }
}
