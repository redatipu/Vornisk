namespace VorniskCli.Core;

/// <summary>
/// Thread-safe token-bucket rate limiter (ported verbatim from the Windows engine — pure
/// managed, no P/Invoke). Rate &lt;= 0 = unlimited: <see cref="ConsumeAsync"/> returns at once.
/// A single request larger than capacity drains a full bucket and proceeds (no deadlock).
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private double _maxBytesPerSec;
    private double _tokens;
    private long   _lastRefillTick;
    private readonly object _lock = new();

    public TokenBucketRateLimiter(double maxBytesPerSec)
    {
        _maxBytesPerSec = maxBytesPerSec;
        _tokens         = maxBytesPerSec;
        _lastRefillTick = Environment.TickCount64;
    }

    public async Task ConsumeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan waitTime;
            lock (_lock)
            {
                Refill();
                if (_maxBytesPerSec <= 0 || _tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                if (bytes >= _maxBytesPerSec && _tokens >= _maxBytesPerSec)
                {
                    _tokens = 0;
                    return;
                }
                double deficit = bytes - _tokens;
                if (bytes > _maxBytesPerSec)
                    deficit = _maxBytesPerSec - _tokens;
                waitTime = TimeSpan.FromSeconds(deficit / _maxBytesPerSec);
            }
            await Task.Delay(waitTime < TimeSpan.FromMilliseconds(1)
                ? TimeSpan.FromMilliseconds(1) : waitTime, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now       = Environment.TickCount64;
        double elapsed = (now - _lastRefillTick) / 1000.0;
        _lastRefillTick = now;
        if (_maxBytesPerSec > 0)
            _tokens = Math.Min(_tokens + elapsed * _maxBytesPerSec, _maxBytesPerSec);
    }
}
