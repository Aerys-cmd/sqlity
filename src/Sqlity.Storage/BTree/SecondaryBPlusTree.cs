using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

/// <summary>
/// A B+ tree keyed on variable-length byte arrays (produced by <see cref="IndexKeyEncoder"/>).
/// Mirrors the structure and algorithms of <see cref="BPlusTree"/> but uses
/// <see cref="IndexLeafPage"/> and <see cref="IndexInternalPage"/> with lexicographic key order.
/// </summary>
internal sealed class SecondaryBPlusTree
{
    private readonly IPager _pager;
    private readonly uint _rootPageId;

    private static readonly int MaxLeafCellBytes =
        DbConstants.PageSize - PageHeader.Size - BTreePageLayout.CellPointerSize;

    public SecondaryBPlusTree(IPager pager, uint rootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
        _rootPageId = rootPageId;
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a (key, primaryKey) pair. For unique indexes the caller should pass
    /// the raw column-value encoding as <paramref name="key"/>; for non-unique indexes
    /// the PK must already be appended to <paramref name="key"/> to guarantee uniqueness.
    /// </summary>
    /// <param name="isUnique">
    /// When true, a duplicate key signals a unique-constraint violation. When false,
    /// the PK is already embedded in the key so duplicates are impossible; a
    /// <see cref="IndexLeafInsertStatus.DuplicateKey"/> result still throws.
    /// </param>
    public void Insert(byte[] key, long primaryKey, bool isUnique)
    {
        var cell = new IndexLeafCell(key, primaryKey);
        if (cell.GetRequiredSize() > MaxLeafCellBytes)
            throw new InvalidOperationException($"Index key ({cell.GetRequiredSize()} bytes) is too large for a single page.");

        var (leaf, leafPageId, ancestors) = FindLeaf(key);
        var status = leaf.TryInsert(cell);

        if (status == IndexLeafInsertStatus.DuplicateKey)
        {
            if (isUnique)
                throw new InvalidOperationException($"Unique index constraint violated: duplicate key.");
            throw new InvalidOperationException("Duplicate index entry (key + PK collision).");
        }

        if (status == IndexLeafInsertStatus.Success)
        {
            _pager.WritePage(leaf.Page);
            return;
        }

        SplitLeaf(leaf, leafPageId, cell, ancestors);
    }

    public void Delete(byte[] key)
    {
        var (leaf, leafPageId, ancestors) = FindLeaf(key);
        var status = leaf.TryDelete(key);

        if (status == IndexLeafDeleteStatus.NotFound)
            throw new InvalidOperationException("Index entry not found during delete.");

        _pager.WritePage(leaf.Page);

        if (leaf.CellCount == 0 && ancestors.Count > 0)
            ReclaimEmptyLeaf(leafPageId, leaf, ancestors);
    }

    public bool TryGet(byte[] key, out IndexLeafCell cell)
    {
        var (leaf, _, _) = FindLeaf(key);
        return leaf.TryGetCell(key, out cell);
    }

    /// <summary>
    /// Releases every page belonging to this secondary B+ tree back to the pager's free list.
    /// After this call the tree is no longer usable.
    /// </summary>
    public void ReleaseAllPages()
    {
        var collected = new HashSet<uint>();
        var toVisit = new Stack<uint>();
        toVisit.Push(_rootPageId);

        while (toVisit.Count > 0)
        {
            var pageId = toVisit.Pop();
            if (!collected.Add(pageId))
                continue;

            var page = _pager.ReadPage(pageId);
            var pageType = page.ReadHeader().PageType;

            if (pageType == PageType.IndexInternal)
            {
                var internalPage = new IndexInternalPage(page);
                toVisit.Push(internalPage.LeftmostChildPageId);
                foreach (var cell in internalPage.ReadAllCells())
                    toVisit.Push(cell.RightChildPageId);
            }
            else if (pageType == PageType.IndexLeaf)
            {
                var next = new IndexLeafPage(page).NextLeafPageId;
                if (next != 0)
                    toVisit.Push(next);
            }
        }

        foreach (var pageId in collected)
            _pager.ReleasePage(pageId);
    }

    /// <summary>
    /// Scans the leaf chain and returns all entries whose keys fall within
    /// <paramref name="range"/>. Results are in ascending key order.
    /// </summary>
    public IReadOnlyList<IndexLeafCell> RangeSeek(IndexSeekRange range)
    {
        var results = new List<IndexLeafCell>();

        var startPageId = range.LowerKey is not null
            ? FindLeafPageIdForKey(range.LowerKey)
            : FindFirstLeafPageId();

        var currentPageId = startPageId;
        while (currentPageId != 0)
        {
            var leaf = new IndexLeafPage(_pager.ReadPage(currentPageId));
            var cells = leaf.ReadAllCells();

            foreach (var cell in cells)
            {
                if (range.ExceedsUpperBound(cell.Key))
                    return results;

                if (range.Contains(cell.Key))
                    results.Add(cell);
            }

            currentPageId = leaf.NextLeafPageId;
        }

        return results;
    }

    // ── Traversal ────────────────────────────────────────────────────────────────

    private readonly record struct Ancestor(uint PageId);

    private (IndexLeafPage Leaf, uint LeafPageId, Stack<Ancestor> Ancestors) FindLeaf(ReadOnlySpan<byte> searchKey)
    {
        var ancestors = new Stack<Ancestor>();
        var currentPageId = _rootPageId;

        while (true)
        {
            var page = _pager.ReadPage(currentPageId);
            var pageType = page.ReadHeader().PageType;

            if (pageType == PageType.IndexLeaf)
                return (new IndexLeafPage(page), currentPageId, ancestors);

            if (pageType == PageType.IndexInternal)
            {
                var internalPage = new IndexInternalPage(page);
                ancestors.Push(new Ancestor(currentPageId));
                currentPageId = internalPage.FindChildPageId(searchKey);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected page type {pageType} in secondary B+ tree traversal.");
            }
        }
    }

    private uint FindLeafPageIdForKey(ReadOnlySpan<byte> key)
    {
        var (_, id, _) = FindLeaf(key);
        return id;
    }

    private uint FindFirstLeafPageId()
    {
        var currentPageId = _rootPageId;

        while (true)
        {
            var page = _pager.ReadPage(currentPageId);
            var header = page.ReadHeader();

            if (header.PageType == PageType.IndexLeaf)
                return currentPageId;

            currentPageId = new IndexInternalPage(page).LeftmostChildPageId;
        }
    }

    // ── Splits ───────────────────────────────────────────────────────────────────

    private void SplitLeaf(
        IndexLeafPage leaf,
        uint leafPageId,
        IndexLeafCell newCell,
        Stack<Ancestor> ancestors)
    {
        var allCells = leaf.ReadAllCells()
            .Append(newCell)
            .OrderBy(c => c.Key, ByteArrayComparer.Instance)
            .ToList();

        var splitIndex = FindLeafSplitIndex(allCells);
        var separatorKey = allCells[splitIndex].Key;
        var originalNext = leaf.NextLeafPageId;

        if (ancestors.Count == 0)
        {
            var leftPageId = _pager.AllocatePage(PageType.IndexLeaf);
            var rightPageId = _pager.AllocatePage(PageType.IndexLeaf);

            var leftLeaf = new IndexLeafPage(_pager.ReadPage(leftPageId));
            var rightLeaf = new IndexLeafPage(_pager.ReadPage(rightPageId));

            leftLeaf.SetNextLeafPageId(rightPageId);
            rightLeaf.SetNextLeafPageId(originalNext);

            InsertAllChecked(leftLeaf, allCells.Take(splitIndex));
            InsertAllChecked(rightLeaf, allCells.Skip(splitIndex));

            _pager.WritePage(leftLeaf.Page);
            _pager.WritePage(rightLeaf.Page);

            var rootInternal = IndexInternalPage.Create(_rootPageId, leftmostChildPageId: leftPageId);
            rootInternal.TryInsert(new IndexInternalCell(separatorKey, rightPageId));
            _pager.WritePage(rootInternal.Page);
        }
        else
        {
            var rightPageId = _pager.AllocatePage(PageType.IndexLeaf);
            var rightLeaf = new IndexLeafPage(_pager.ReadPage(rightPageId));

            rightLeaf.SetNextLeafPageId(originalNext);
            InsertAllChecked(rightLeaf, allCells.Skip(splitIndex));

            var leftLeaf = IndexLeafPage.Create(leafPageId);
            leftLeaf.SetNextLeafPageId(rightPageId);
            InsertAllChecked(leftLeaf, allCells.Take(splitIndex));

            _pager.WritePage(leftLeaf.Page);
            _pager.WritePage(rightLeaf.Page);

            var parent = ancestors.Pop();
            InsertIntoInternal(parent.PageId, new IndexInternalCell(separatorKey, rightPageId), ancestors);
        }
    }

    private void InsertIntoInternal(uint pageId, IndexInternalCell cell, Stack<Ancestor> ancestors)
    {
        var page = new IndexInternalPage(_pager.ReadPage(pageId));
        var status = page.TryInsert(cell);

        if (status == IndexInternalInsertStatus.DuplicateKey)
            throw new InvalidOperationException($"Duplicate divider key in index internal page — B-tree invariant violated.");

        if (status == IndexInternalInsertStatus.Success)
        {
            _pager.WritePage(page.Page);
            return;
        }

        SplitInternal(page, pageId, cell, ancestors);
    }

    private void SplitInternal(
        IndexInternalPage internalPage,
        uint internalPageId,
        IndexInternalCell newCell,
        Stack<Ancestor> ancestors)
    {
        var allCells = internalPage.ReadAllCells()
            .Append(newCell)
            .OrderBy(c => c.DividerKey, ByteArrayComparer.Instance)
            .ToList();

        var medianIndex = allCells.Count / 2;
        var medianCell = allCells[medianIndex];
        var originalLeftmost = internalPage.LeftmostChildPageId;

        var rightPageId = _pager.AllocatePage(PageType.IndexInternal);
        var rightInternal = IndexInternalPage.Create(rightPageId, leftmostChildPageId: medianCell.RightChildPageId);
        foreach (var c in allCells.Skip(medianIndex + 1))
            AssertInternalInsert(rightInternal.TryInsert(c), c.DividerKey);

        _pager.WritePage(rightInternal.Page);

        if (ancestors.Count == 0)
        {
            var leftCopyPageId = _pager.AllocatePage(PageType.IndexInternal);
            var leftCopy = IndexInternalPage.Create(leftCopyPageId, leftmostChildPageId: originalLeftmost);
            foreach (var c in allCells.Take(medianIndex))
                AssertInternalInsert(leftCopy.TryInsert(c), c.DividerKey);

            _pager.WritePage(leftCopy.Page);

            var newRoot = IndexInternalPage.Create(_rootPageId, leftmostChildPageId: leftCopyPageId);
            newRoot.TryInsert(new IndexInternalCell(medianCell.DividerKey, rightPageId));
            _pager.WritePage(newRoot.Page);
        }
        else
        {
            var leftInternal = IndexInternalPage.Create(internalPageId, leftmostChildPageId: originalLeftmost);
            foreach (var c in allCells.Take(medianIndex))
                AssertInternalInsert(leftInternal.TryInsert(c), c.DividerKey);

            _pager.WritePage(leftInternal.Page);

            var parent = ancestors.Pop();
            InsertIntoInternal(parent.PageId, new IndexInternalCell(medianCell.DividerKey, rightPageId), ancestors);
        }
    }

    // ── Reclaim ──────────────────────────────────────────────────────────────────

    private void ReclaimEmptyLeaf(uint emptyLeafPageId, IndexLeafPage emptyLeaf, Stack<Ancestor> ancestors)
    {
        var firstLeafPageId = FindFirstLeafPageId();
        if (firstLeafPageId != emptyLeafPageId)
        {
            var currentPageId = firstLeafPageId;
            while (currentPageId != 0)
            {
                var current = new IndexLeafPage(_pager.ReadPage(currentPageId));
                if (current.NextLeafPageId == emptyLeafPageId)
                {
                    current.SetNextLeafPageId(emptyLeaf.NextLeafPageId);
                    _pager.WritePage(current.Page);
                    break;
                }
                currentPageId = current.NextLeafPageId;
            }
        }

        var parentPageId = ancestors.Pop().PageId;
        var parent = new IndexInternalPage(_pager.ReadPage(parentPageId));
        var removeStatus = parent.TryRemoveChildReference(emptyLeafPageId);

        if (removeStatus == RemoveChildStatus.LastChildRemoved)
        {
            if (ancestors.Count == 0)
            {
                var emptyRoot = IndexLeafPage.Create(_rootPageId);
                _pager.WritePage(emptyRoot.Page);
            }
        }
        else
        {
            _pager.WritePage(parent.Page);
        }

        _pager.ReleasePage(emptyLeafPageId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static int FindLeafSplitIndex(List<IndexLeafCell> cells)
    {
        var totalBytes = cells.Sum(c => c.GetRequiredSize() + BTreePageLayout.CellPointerSize);
        var halfBytes = totalBytes / 2;
        var cumulative = 0;

        for (var i = 0; i < cells.Count - 1; i++)
        {
            cumulative += cells[i].GetRequiredSize() + BTreePageLayout.CellPointerSize;
            if (cumulative >= halfBytes) return i + 1;
        }

        return cells.Count / 2;
    }

    private static void InsertAllChecked(IndexLeafPage page, IEnumerable<IndexLeafCell> cells)
    {
        foreach (var cell in cells)
        {
            var status = page.TryInsert(cell);
            if (status != IndexLeafInsertStatus.Success)
                throw new InvalidOperationException($"Failed to insert index cell during leaf split: {status}.");
        }
    }

    private static void AssertInternalInsert(IndexInternalInsertStatus status, byte[] key)
    {
        if (status != IndexInternalInsertStatus.Success)
            throw new InvalidOperationException($"Failed to insert divider key into index internal page: {status}.");
    }

    // ── Key comparer for OrderBy ──────────────────────────────────────────────────

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return ((ReadOnlySpan<byte>)x).SequenceCompareTo(y);
        }
    }
}
