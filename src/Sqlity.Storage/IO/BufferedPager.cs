using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.IO;

/// <summary>
/// A write-through, LRU-eviction page cache that wraps any <see cref="IPager"/>.
/// Hot pages are served from an in-memory dictionary; dirty pages are flushed to the
/// inner pager on eviction or when a transaction commits/rolls back.
/// </summary>
public sealed class BufferedPager : IPager
{
    private readonly IPager _inner;
    private readonly int _capacity;

    // LRU bookkeeping — doubly-linked list with a head (most-recent) sentinel.
    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<uint, LinkedListNode<CacheEntry>> _cache = new();

    public BufferedPager(IPager inner, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        _inner = inner;
        _capacity = capacity;
    }

    public bool InTransaction => _inner.InTransaction;

    public void InitializeNew() => _inner.InitializeNew();

    public DatabaseHeader ReadDatabaseHeader() => _inner.ReadDatabaseHeader();

    public void WriteDatabaseHeader(in DatabaseHeader header) => _inner.WriteDatabaseHeader(in header);

    public PageBuffer ReadPage(uint pageNumber)
    {
        if (_cache.TryGetValue(pageNumber, out var node))
        {
            // Cache hit — move to front of LRU list and return a copy.
            MoveToFront(node);
            return CopyPage(node.Value.Page);
        }

        // Cache miss — read from inner pager, insert into cache.
        var page = _inner.ReadPage(pageNumber);
        InsertIntoCache(page, isDirty: false);
        return CopyPage(page);
    }

    public void WritePage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (_cache.TryGetValue(page.PageNumber, out var existing))
        {
            // Update the cached copy and mark dirty.
            CopyPageData(page, existing.Value.Page);
            existing.Value.IsDirty = true;
            MoveToFront(existing);
        }
        else
        {
            InsertIntoCache(CopyPage(page), isDirty: true);
        }
    }

    public uint AllocatePage(PageType pageType)
    {
        // AllocatePage writes both the new page and the header through to the inner pager.
        // We bypass the cache here and let the natural WritePage path handle caching.
        var pageNumber = _inner.AllocatePage(pageType);

        // Invalidate any stale cached copy for this page number (recycled page case).
        InvalidateCachedPage(pageNumber);

        return pageNumber;
    }

    public void ReleasePage(uint pageNumber)
    {
        // Evict from cache and release through to inner pager.
        InvalidateCachedPage(pageNumber);
        _inner.ReleasePage(pageNumber);
    }

    public void BeginTransaction()
    {
        _inner.BeginTransaction();
    }

    public void Commit()
    {
        FlushDirtyPages();
        _inner.Commit();
    }

    public void Rollback()
    {
        // Discard all cached pages — the inner pager will restore the original state.
        _lruList.Clear();
        _cache.Clear();
        _inner.Rollback();
    }

    public void Dispose()
    {
        // Best-effort flush of dirty pages before disposing.
        try { FlushDirtyPages(); } catch { /* swallow — we're disposing */ }
        _inner.Dispose();
    }

    // ── LRU helpers ─────────────────────────────────────────────────────────

    private void InsertIntoCache(PageBuffer page, bool isDirty)
    {
        if (_cache.Count >= _capacity)
            EvictLeastRecentlyUsed();

        var entry = new CacheEntry(page) { IsDirty = isDirty };
        var node = _lruList.AddFirst(entry);
        _cache[page.PageNumber] = node;
    }

    private void MoveToFront(LinkedListNode<CacheEntry> node)
    {
        _lruList.Remove(node);
        _lruList.AddFirst(node);
    }

    private void EvictLeastRecentlyUsed()
    {
        var lru = _lruList.Last;
        if (lru is null)
            return;

        if (lru.Value.IsDirty)
            _inner.WritePage(lru.Value.Page);

        _cache.Remove(lru.Value.Page.PageNumber);
        _lruList.RemoveLast();
    }

    private void InvalidateCachedPage(uint pageNumber)
    {
        if (_cache.TryGetValue(pageNumber, out var node))
        {
            _lruList.Remove(node);
            _cache.Remove(pageNumber);
        }
    }

    private void FlushDirtyPages()
    {
        foreach (var node in _lruList)
        {
            if (node.IsDirty)
            {
                _inner.WritePage(node.Page);
                node.IsDirty = false;
            }
        }
    }

    private static PageBuffer CopyPage(PageBuffer source)
    {
        var bytes = new byte[source.ReadOnlySpan.Length];
        source.ReadOnlySpan.CopyTo(bytes);
        return new PageBuffer(source.PageNumber, bytes);
    }

    private static void CopyPageData(PageBuffer source, PageBuffer destination)
    {
        source.ReadOnlySpan.CopyTo(destination.Span);
    }

    private sealed class CacheEntry(PageBuffer page)
    {
        public PageBuffer Page { get; } = page;
        public bool IsDirty { get; set; }
    }
}
