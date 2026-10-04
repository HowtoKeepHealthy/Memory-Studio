using System.Diagnostics;

namespace MemoryStudio;

public sealed record MemoryChange(ulong Address, byte[] Before, byte[] After, bool Code);
public sealed record MemoryUndoResult(int Attempted, int Restored, IReadOnlyList<string> Errors, string Description, IReadOnlyList<MemoryChange> Changes);

/// <summary>Shared by all windows attached to the same live process. Freeze refreshes are never recorded.</summary>
public sealed class MemoryEditHistory
{
    private sealed record Entry(string Description, DateTime Timestamp, List<MemoryChange> Changes);
    private static readonly object SharedGate = new();
    private static readonly Dictionary<(int Pid, long Started), WeakReference<MemoryEditHistory>> Shared = [];
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private Entry? _batch;
    private readonly AsyncLocal<Entry?> _batchContext = new();
    private long _bytes;
    private readonly Dictionary<WatchRow, ulong> _trackedWatches = [];
    private readonly SortedSet<ulong> _frozenAddresses = [];
    private readonly Dictionary<ulong, HashSet<WatchRow>> _frozenWatches = [];
    private const int MaximumSteps = 100;
    private const long MaximumBytes = 32 * 1024 * 1024;
    private volatile int _publishedCount;
    private volatile string _publishedDescription = "";
    private long _publishedTimestamp;
    public bool CanUndo => _publishedCount > 0;
    public string UndoDescription => _publishedDescription;
    public DateTime LatestTimestamp => new(Interlocked.Read(ref _publishedTimestamp), DateTimeKind.Utc);
    public int Count => _publishedCount;
    private void PublishState()
    {
        _publishedDescription = _entries.LastOrDefault()?.Description ?? "";
        Interlocked.Exchange(ref _publishedTimestamp, _entries.LastOrDefault()?.Timestamp.Ticks ?? 0);
        _publishedCount = _entries.Count;
    }
    public void TrackWatch(WatchRow watch)
    {
        lock (_gate)
        {
            UntrackWatch(watch);
            if (!watch.IsFrozen) return;
            _trackedWatches[watch] = watch.Address; _frozenAddresses.Add(watch.Address);
            if (!_frozenWatches.TryGetValue(watch.Address, out var rows)) _frozenWatches[watch.Address] = rows = [];
            rows.Add(watch);
        }
    }
    public void UntrackWatch(WatchRow watch)
    {
        lock (_gate)
        {
            if (!_trackedWatches.Remove(watch, out ulong address)) return;
            if (_frozenWatches.TryGetValue(address, out var rows))
            { rows.Remove(watch); if (rows.Count == 0) { _frozenWatches.Remove(address); _frozenAddresses.Remove(address); } }
        }
    }
    public void MaintainFrozen(NativeEngine engine, WatchRow watch)
    {
        lock (_gate) { if (watch.IsFrozen) engine.Write(watch.Address, watch.FrozenValue); }
    }
    private void UpdateFrozenTargets(NativeEngine engine, ulong address, int length)
    {
        if (_frozenAddresses.Count == 0) return;
        ulong first = address >= 4095 ? address - 4095 : 0;
        ulong last = address > ulong.MaxValue - (uint)(length - 1) ? ulong.MaxValue : address + (uint)(length - 1);
        foreach (ulong start in _frozenAddresses.GetViewBetween(first, last).ToArray())
            foreach (var watch in _frozenWatches[start].ToArray())
            {
                if (!watch.IsFrozen || start > address && start > last || start <= address && address - start >= (ulong)watch.Size) continue;
                try { watch.FrozenValue = engine.Read(watch.Address, watch.Size); }
                catch { watch.IsFrozen = false; }
            }
    }

    public static MemoryEditHistory ForProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        long started = process.StartTime.ToUniversalTime().Ticks;
        lock (SharedGate)
        {
            var key = (processId, started);
            if (Shared.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing)) return existing;
            var history = new MemoryEditHistory();
            Shared[key] = new(history);
            foreach (var expired in Shared.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToArray()) Shared.Remove(expired);
            return history;
        }
    }

    public IDisposable BeginBatch(string description)
    {
        lock (_gate)
        {
            if (_batch != null) throw new InvalidOperationException("另一组内存修改尚未完成。");
            _batch = new(description, DateTime.UtcNow, []);
            _batchContext.Value = _batch;
            return new BatchScope(this);
        }
    }
    private sealed class BatchScope(MemoryEditHistory owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                var entry = owner._batch; owner._batch = null;
                owner._batchContext.Value = null;
                if (entry is { Changes.Count: > 0 }) owner.Add(entry with { Timestamp = DateTime.UtcNow });
            }
        }
    }

    public void Write(NativeEngine engine, ulong address, byte[] bytes, string description, bool code = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (bytes.Length is < 1 or > 1024 * 1024) throw new ArgumentException("单次内存修改长度须在 1 字节至 1 MB 之间。");
        lock (_gate)
        {
            if (_batch != null && _batchContext.Value != _batch) throw new InvalidOperationException("此进程的另一组写入正在进行，请等待完成后再修改。");
            byte[] original = engine.Read(address, bytes.Length);
            if (original.SequenceEqual(bytes)) { UpdateFrozenTargets(engine, address, bytes.Length); return; }
            // Record only after successful writes. Failed or partial writes preserve a recovery entry when readable.
            try { if (code) engine.WriteCode(address, bytes); else engine.Write(address, bytes); }
            catch
            {
                try
                {
                    byte[] current = engine.Read(address, bytes.Length);
                    if (!original.SequenceEqual(current)) { Record(new(address, original, current, code), description); UpdateFrozenTargets(engine, address, current.Length); }
                }
                catch { }
                throw;
            }
            Record(new(address, original, bytes.ToArray(), code), description);
            // Serialize freeze maintenance with edits from every browser. Updating this atomic
            // value property also lets WPF marshal its PropertyChanged notification to the UI.
            UpdateFrozenTargets(engine, address, bytes.Length);
        }
    }
    private void Record(MemoryChange change, string description)
    {
        if (_batch != null) _batch.Changes.Add(change);
        else Add(new(description, DateTime.UtcNow, [change]));
    }
    private void Add(Entry entry)
    {
        _entries.Add(entry);
        _bytes += entry.Changes.Sum(c => (long)c.Before.Length + c.After.Length);
        while (_entries.Count > MaximumSteps || (_bytes > MaximumBytes && _entries.Count > 1))
        {
            _bytes -= _entries[0].Changes.Sum(c => (long)c.Before.Length + c.After.Length);
            _entries.RemoveAt(0);
        }
        PublishState();
    }
    public MemoryUndoResult Undo(NativeEngine engine)
    {
        lock (_gate)
        {
            if (_batch != null) throw new InvalidOperationException("请等待当前修改完成后再撤销。");
            if (_entries.Count == 0) return new(0, 0, [], "", []);
            var entry = _entries[^1];
            var restored = new List<MemoryChange>();
            var failed = new List<MemoryChange>();
            var errors = new List<string>();
            foreach (var change in entry.Changes.AsEnumerable().Reverse())
            {
                if (failed.Any(later => Overlaps(change, later)))
                { failed.Add(change); errors.Add($"0x{change.Address:X}: 后续重叠修改未恢复，保留此步供重试。"); continue; }
                try
                {
                    if (change.Code) engine.WriteCode(change.Address, change.Before); else engine.Write(change.Address, change.Before);
                    restored.Add(change);
                    UpdateFrozenTargets(engine, change.Address, change.Before.Length);
                }
                catch (Exception ex)
                {
                    // A code write can fail while restoring protection after bytes changed.
                    // Keep the retry step and synchronize freezes with the bytes that actually remain.
                    UpdateFrozenTargets(engine, change.Address, change.Before.Length);
                    failed.Add(change); errors.Add($"0x{change.Address:X}: {ex.Message}");
                }
            }
            _entries.RemoveAt(_entries.Count - 1);
            _bytes -= entry.Changes.Sum(c => (long)c.Before.Length + c.After.Length);
            if (failed.Count > 0) Add(entry with { Changes = failed.AsEnumerable().Reverse().ToList() });
            PublishState();
            return new(entry.Changes.Count, restored.Count, errors, entry.Description, restored);
        }
    }
    private static bool Overlaps(MemoryChange left, MemoryChange right) =>
        left.Address <= right.Address ? right.Address - left.Address < (ulong)left.Before.Length : left.Address - right.Address < (ulong)right.Before.Length;
}
