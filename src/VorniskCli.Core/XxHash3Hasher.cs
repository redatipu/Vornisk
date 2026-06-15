using System.IO.Hashing;

namespace VorniskCli.Core;

/// <summary>xxHash3 streaming hasher (System.IO.Hashing — fully cross-platform).</summary>
public sealed class XxHash3Hasher
{
    private readonly XxHash3 _hash = new();

    public void   Append(ReadOnlySpan<byte> data) => _hash.Append(data);
    public byte[] GetCurrentHash() => _hash.GetCurrentHash();
    public void   Reset() => _hash.Reset();
}
