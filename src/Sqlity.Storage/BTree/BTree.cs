using System.Buffers.Binary;
using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

/// <summary>
/// A B+ tree built on top of <see cref="IPager"/>. The root page ID is stable for the
/// lifetime of the tree because root splits are handled in-place: when the root overflows,
/// its content is moved to freshly allocated pages and the root is reformatted as an internal
/// page. This avoids any catalog update on splits.
/// </summary>
internal sealed class BPlusTree
{
    private readonly IPager _pager;
    private readonly uint _rootPageId;

    // Maximum bytes a single leaf cell may occupy on a page (cell + pointer slot).
    private static readonly int MaxLeafCellBytes =
        DbConstants.PageSize - PageHeader.Size - BTreePageLayout.CellPointerSize;

    // Maximum payload bytes that can be stored inline (without overflow) for a minimal cell
    // containing just the key and payload (header + payload, no cell-pointer overhead here
    // because the pointer list grows from the opposite end of the page).
    private static readonly int InlinePayloadCapacity =
        MaxLeafCellBytes - BTreePageLayout.LeafCellHeaderSize;

    public BPlusTree(IPager pager, uint rootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
        _rootPageId = rootPageId;
    }

    // ── Public DML ──────────────────────────────────────────────────────────────

    public void Insert(TableLeafCell cell)
    {
        // Rows that exceed the usable leaf space are spilled to chained overflow pages;
        // only an 18-byte pointer cell is stored on the leaf page itself.
        if (cell.GetRequiredSize() > MaxLeafCellBytes)
        {
            var firstOverflowPageId = SpillToOverflow(cell.Payload);
            var pointerCell = BuildOverflowPointerCell(cell.PrimaryKey, cell.Payload.Length, firstOverflowPageId);
            try
            {
                InsertCell(pointerCell);
            }
            catch
            {
                // If the insert failed, release the already-allocated overflow pages so they
                // don't become orphaned.
                ReleaseOverflowChain(firstOverflowPageId, cell.Payload.Length);
                throw;
            }
            return;
        }

        InsertCell(cell);
    }

    private void InsertCell(TableLeafCell cell)
    {
        var (leaf, leafPageId, ancestors) = FindLeaf(cell.PrimaryKey);
        var status = leaf.TryInsert(cell);

        if (status == TableLeafInsertStatus.DuplicateKey)
            throw new InvalidOperationException($"Duplicate primary key {cell.PrimaryKey}.");

        if (status == TableLeafInsertStatus.Success)
        {
            _pager.WritePage(leaf.Page);
            return;
        }

        SplitLeaf(leaf, leafPageId, cell, ancestors);
    }

    public void Delete(long primaryKey)
    {
        var (leaf, leafPageId, ancestors) = FindLeaf(primaryKey);

        // If the stored cell is an overflow pointer, release the overflow chain before
        // removing the pointer cell so pages are always reclaimed even if an exception occurs.
        if (leaf.TryGetCell(primaryKey, out var existingCell) && existingCell.IsOverflowPointer)
        {
            var (totalSize, firstPageId) = ReadOverflowPointer(existingCell);
            ReleaseOverflowChain(firstPageId, totalSize);
        }

        var status = leaf.TryDelete(primaryKey);

        if (status == TableLeafDeleteStatus.NotFound)
            throw new InvalidOperationException($"Primary key {primaryKey} not found.");

        _pager.WritePage(leaf.Page);

        if (leaf.CellCount == 0 && ancestors.Count > 0)
            ReclaimEmptyLeaf(leafPageId, leaf, ancestors);
    }

    public bool TryGet(long primaryKey, out TableLeafCell cell)
    {
        var (leaf, _, _) = FindLeaf(primaryKey);
        if (!leaf.TryGetCell(primaryKey, out var raw))
        {
            cell = default;
            return false;
        }
        cell = MaterializeCell(raw);
        return true;
    }

    public void Update(TableLeafCell cell)
    {
        var (leaf, leafPageId, ancestors) = FindLeaf(cell.PrimaryKey);

        if (!leaf.TryGetCell(cell.PrimaryKey, out var existing))
            throw new InvalidOperationException($"Primary key {cell.PrimaryKey} not found.");

        bool newNeedsOverflow = cell.Payload.Length > InlinePayloadCapacity;
        bool oldWasOverflow = existing.IsOverflowPointer;

        if (!newNeedsOverflow && !oldWasOverflow)
        {
            // Inline → Inline: attempt in-place update; fall back to delete+insert if the
            // new payload doesn't fit in the space already occupied by the old cell.
            var status = leaf.TryUpdate(cell);
            if (status == TableLeafUpdateStatus.Success)
            {
                _pager.WritePage(leaf.Page);
                return;
            }

            // InsufficientSpace: delete old then re-insert (may trigger a leaf split).
            leaf.TryDelete(cell.PrimaryKey);
            _pager.WritePage(leaf.Page);
            InsertCell(cell);
            return;
        }

        if (newNeedsOverflow && oldWasOverflow)
        {
            // Overflow → Overflow: allocate new chain first, then overwrite pointer in-place,
            // then free old chain. This keeps the leaf consistent even if free-chain updates fail.
            var (oldTotal, oldFirstPageId) = ReadOverflowPointer(existing);
            var newFirstPageId = SpillToOverflow(cell.Payload);
            var newPointerCell = BuildOverflowPointerCell(cell.PrimaryKey, cell.Payload.Length, newFirstPageId);

            // Overwrite the 18-byte pointer cell in-place; it is always the same size.
            var updateStatus = leaf.TryUpdate(newPointerCell);
            if (updateStatus != TableLeafUpdateStatus.Success)
                throw new InvalidOperationException($"Overflow pointer update failed for key {cell.PrimaryKey}: {updateStatus}.");

            _pager.WritePage(leaf.Page);
            ReleaseOverflowChain(oldFirstPageId, oldTotal);
            return;
        }

        if (newNeedsOverflow && !oldWasOverflow)
        {
            // Inline → Overflow
            var firstOverflowPageId = SpillToOverflow(cell.Payload);
            var pointerCell = BuildOverflowPointerCell(cell.PrimaryKey, cell.Payload.Length, firstOverflowPageId);
            try
            {
                leaf.TryDelete(cell.PrimaryKey);
                _pager.WritePage(leaf.Page);
                InsertCell(pointerCell);
            }
            catch
            {
                ReleaseOverflowChain(firstOverflowPageId, cell.Payload.Length);
                throw;
            }
            return;
        }

        // Overflow → Inline: delete pointer cell, insert new inline cell, THEN release old
        // chain. Releasing after a successful insert ensures overflow pages are not orphaned
        // if InsertCell throws (e.g. during a leaf split). The caller's transaction rollback
        // will restore the leaf page in that case.
        {
            var (oldTotal, oldFirstPageId) = ReadOverflowPointer(existing);
            leaf.TryDelete(cell.PrimaryKey);
            _pager.WritePage(leaf.Page);
            InsertCell(cell);
            ReleaseOverflowChain(oldFirstPageId, oldTotal);
        }
    }

    /// <summary>
    /// Reads all cells in ascending key order by following the leftmost path to the
    /// first leaf and then traversing the leaf chain.
    /// </summary>
    public IReadOnlyList<TableLeafCell> ReadAll()
    {
        var firstLeafPageId = FindFirstLeafPageId();
        var results = new List<TableLeafCell>();
        var currentPageId = firstLeafPageId;

        while (currentPageId != 0)
        {
            var leaf = new TableLeafPage(_pager.ReadPage(currentPageId));
            foreach (var raw in leaf.ReadAllCells())
                results.Add(MaterializeCell(raw));
            currentPageId = leaf.NextLeafPageId;
        }

        return results;
    }

    // ── Traversal ───────────────────────────────────────────────────────────────

    private readonly record struct BTreeAncestor(uint PageId);

    /// <summary>
    /// Walks from the root to the leaf page that would contain <paramref name="searchKey"/>.
    /// Returns the leaf, its page ID, and the ancestor stack (top = immediate parent).
    /// </summary>
    private (TableLeafPage Leaf, uint LeafPageId, Stack<BTreeAncestor> Ancestors) FindLeaf(long searchKey)
    {
        var ancestors = new Stack<BTreeAncestor>();
        var currentPageId = _rootPageId;

        while (true)
        {
            var page = _pager.ReadPage(currentPageId);
            var pageType = page.ReadHeader().PageType;

            if (pageType == PageType.TableLeaf)
            {
                return (new TableLeafPage(page), currentPageId, ancestors);
            }

            if (pageType == PageType.TableInternal)
            {
                var internalPage = new TableInternalPage(page);
                ancestors.Push(new BTreeAncestor(currentPageId));
                currentPageId = internalPage.FindChildPageId(searchKey);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unexpected page type {pageType} encountered during B-tree traversal.");
            }
        }
    }

    private uint FindFirstLeafPageId()
    {
        var currentPageId = _rootPageId;

        while (true)
        {
            var page = _pager.ReadPage(currentPageId);
            var header = page.ReadHeader();

            if (header.PageType == PageType.TableLeaf)
            {
                return currentPageId;
            }

            var internalPage = new TableInternalPage(page);
            currentPageId = internalPage.LeftmostChildPageId;
        }
    }

    // ── Splits ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a full leaf page. When the leaf is the root, two new leaf pages are
    /// allocated and the root is reformatted as an internal page — no circular references.
    /// When the leaf is not the root, only a right sibling is allocated and the leaf is
    /// rewritten in-place.
    /// </summary>
    private void SplitLeaf(
        TableLeafPage leaf,
        uint leafPageId,
        TableLeafCell newCell,
        Stack<BTreeAncestor> ancestors)
    {
        var allCells = leaf.ReadAllCells()
            .Append(newCell)
            .OrderBy(c => c.PrimaryKey)
            .ToList();

        var splitIndex = FindLeafSplitIndex(allCells);
        var separatorKey = allCells[splitIndex].PrimaryKey;
        var originalNext = leaf.NextLeafPageId;

        if (ancestors.Count == 0)
        {
            // Root is a leaf — allocate TWO new leaf pages so the root page itself can be
            // reformatted as an internal page, avoiding a self-referential pointer.
            var leftPageId = _pager.AllocatePage(PageType.TableLeaf);
            var rightPageId = _pager.AllocatePage(PageType.TableLeaf);

            var leftLeaf = new TableLeafPage(_pager.ReadPage(leftPageId));
            var rightLeaf = new TableLeafPage(_pager.ReadPage(rightPageId));

            leftLeaf.SetNextLeafPageId(rightPageId);
            rightLeaf.SetNextLeafPageId(originalNext);

            InsertAllChecked(leftLeaf, allCells.Take(splitIndex));
            InsertAllChecked(rightLeaf, allCells.Skip(splitIndex));

            _pager.WritePage(leftLeaf.Page);
            _pager.WritePage(rightLeaf.Page);

            var rootInternal = TableInternalPage.Create(_rootPageId, leftmostChildPageId: leftPageId);
            rootInternal.TryInsert(new TableInternalCell(separatorKey, rightPageId));
            _pager.WritePage(rootInternal.Page);
        }
        else
        {
            // Non-root leaf — allocate one right sibling; rewrite the current page in-place.
            var rightPageId = _pager.AllocatePage(PageType.TableLeaf);
            var rightLeaf = new TableLeafPage(_pager.ReadPage(rightPageId));

            rightLeaf.SetNextLeafPageId(originalNext);
            InsertAllChecked(rightLeaf, allCells.Skip(splitIndex));

            var leftLeaf = TableLeafPage.Create(leafPageId);
            leftLeaf.SetNextLeafPageId(rightPageId);
            InsertAllChecked(leftLeaf, allCells.Take(splitIndex));

            _pager.WritePage(leftLeaf.Page);
            _pager.WritePage(rightLeaf.Page);

            var parent = ancestors.Pop();
            InsertIntoInternal(parent.PageId, new TableInternalCell(separatorKey, rightPageId), ancestors);
        }
    }

    private void InsertIntoInternal(
        uint internalPageId,
        TableInternalCell cell,
        Stack<BTreeAncestor> ancestors)
    {
        var internalPage = new TableInternalPage(_pager.ReadPage(internalPageId));
        var status = internalPage.TryInsert(cell);

        if (status == TableInternalInsertStatus.DuplicateKey)
        {
            throw new InvalidOperationException(
                $"Duplicate divider key {cell.DividerKey} in internal page — B-tree invariant violated.");
        }

        if (status == TableInternalInsertStatus.Success)
        {
            _pager.WritePage(internalPage.Page);
            return;
        }

        SplitInternal(internalPage, internalPageId, cell, ancestors);
    }

    /// <summary>
    /// Splits a full internal page. The median key is pushed up (not copied). When the
    /// page is the root, a new page is allocated for the left half and the root is
    /// reformatted in-place, preserving a stable root page ID.
    /// </summary>
    private void SplitInternal(
        TableInternalPage internalPage,
        uint internalPageId,
        TableInternalCell newCell,
        Stack<BTreeAncestor> ancestors)
    {
        var allCells = internalPage.ReadAllCells()
            .Append(newCell)
            .OrderBy(c => c.DividerKey)
            .ToList();

        var medianIndex = allCells.Count / 2;
        var medianCell = allCells[medianIndex];
        var originalLeftmost = internalPage.LeftmostChildPageId;

        // Right half: entries after the median; leftmost child = median's right child.
        var rightPageId = _pager.AllocatePage(PageType.TableInternal);
        var rightInternal = TableInternalPage.Create(rightPageId, leftmostChildPageId: medianCell.RightChildPageId);
        foreach (var c in allCells.Skip(medianIndex + 1))
        {
            AssertInternalInsert(rightInternal.TryInsert(c), c.DividerKey);
        }

        _pager.WritePage(rightInternal.Page);

        if (ancestors.Count == 0)
        {
            // Root internal — allocate a new page for the left half, reformat root.
            var leftCopyPageId = _pager.AllocatePage(PageType.TableInternal);
            var leftCopy = TableInternalPage.Create(leftCopyPageId, leftmostChildPageId: originalLeftmost);
            foreach (var c in allCells.Take(medianIndex))
            {
                AssertInternalInsert(leftCopy.TryInsert(c), c.DividerKey);
            }

            _pager.WritePage(leftCopy.Page);

            var newRoot = TableInternalPage.Create(_rootPageId, leftmostChildPageId: leftCopyPageId);
            newRoot.TryInsert(new TableInternalCell(medianCell.DividerKey, rightPageId));
            _pager.WritePage(newRoot.Page);
        }
        else
        {
            // Non-root internal — rewrite current page as left half.
            var leftInternal = TableInternalPage.Create(internalPageId, leftmostChildPageId: originalLeftmost);
            foreach (var c in allCells.Take(medianIndex))
            {
                AssertInternalInsert(leftInternal.TryInsert(c), c.DividerKey);
            }

            _pager.WritePage(leftInternal.Page);

            var parent = ancestors.Pop();
            InsertIntoInternal(parent.PageId, new TableInternalCell(medianCell.DividerKey, rightPageId), ancestors);
        }
    }

    /// <summary>
    /// Releases every page belonging to this B+ tree back to the pager's free list.
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

            if (pageType == PageType.TableInternal)
            {
                var internalPage = new TableInternalPage(page);
                toVisit.Push(internalPage.LeftmostChildPageId);
                foreach (var cell in internalPage.ReadAllCells())
                    toVisit.Push(cell.RightChildPageId);
            }
            else if (pageType == PageType.TableLeaf)
            {
                var leafPage = new TableLeafPage(page);
                // Walk overflow chains for any overflow-pointer cells before releasing the leaf.
                foreach (var cell in leafPage.ReadAllCells())
                {
                    if (cell.IsOverflowPointer)
                    {
                        var (totalSize, firstOverflowPageId) = ReadOverflowPointer(cell);
                        ReleaseOverflowChain(firstOverflowPageId, totalSize);
                    }
                }
                var next = leafPage.NextLeafPageId;
                if (next != 0)
                    toVisit.Push(next);
            }
        }

        foreach (var pageId in collected)
            _pager.ReleasePage(pageId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called after a delete has emptied <paramref name="emptyLeafPageId"/>. Updates the
    /// leaf chain, removes the reference from the parent internal page, handles tree
    /// collapse when the parent becomes childless, and releases the now-unused page.
    /// </summary>
    private void ReclaimEmptyLeaf(
        uint emptyLeafPageId,
        TableLeafPage emptyLeaf,
        Stack<BTreeAncestor> ancestors)
    {
        // ── 1. Update the leaf chain ──────────────────────────────────────────
        // Find the predecessor leaf (the one whose NextLeafPageId points to the empty leaf).
        // If the empty leaf is the chain head there is no predecessor page to update.
        var firstLeafPageId = FindFirstLeafPageId();
        if (firstLeafPageId != emptyLeafPageId)
        {
            var currentPageId = firstLeafPageId;
            while (currentPageId != 0)
            {
                var current = new TableLeafPage(_pager.ReadPage(currentPageId));
                if (current.NextLeafPageId == emptyLeafPageId)
                {
                    current.SetNextLeafPageId(emptyLeaf.NextLeafPageId);
                    _pager.WritePage(current.Page);
                    break;
                }

                currentPageId = current.NextLeafPageId;
            }
        }

        // ── 2. Remove child reference from parent ─────────────────────────────
        var parentPageId = ancestors.Pop().PageId;
        var parent = new TableInternalPage(_pager.ReadPage(parentPageId));
        var removeStatus = parent.TryRemoveChildReference(emptyLeafPageId);

        if (removeStatus == RemoveChildStatus.LastChildRemoved)
        {
            // The parent lost its only child. If the parent IS the root, reformat it as an
            // empty leaf so the tree returns to a single-page state.
            if (ancestors.Count == 0)
            {
                var emptyRoot = TableLeafPage.Create(_rootPageId);
                _pager.WritePage(emptyRoot.Page);
            }
            // Non-root parents with 0 children are a known limitation (not collapsed further).
        }
        else
        {
            _pager.WritePage(parent.Page);
        }

        // ── 3. Return the empty leaf to the free list ─────────────────────────
        _pager.ReleasePage(emptyLeafPageId);
    }

    /// <summary>
    /// Chooses a split index based on cumulative byte size so that variable-length payloads
    /// are distributed evenly rather than by count alone.
    /// </summary>
    private static int FindLeafSplitIndex(List<TableLeafCell> cells)
    {
        var totalBytes = cells.Sum(c => c.GetRequiredSize() + BTreePageLayout.CellPointerSize);
        var halfBytes = totalBytes / 2;
        var cumulative = 0;

        for (var i = 0; i < cells.Count - 1; i++)
        {
            cumulative += cells[i].GetRequiredSize() + BTreePageLayout.CellPointerSize;
            if (cumulative >= halfBytes)
            {
                return i + 1;
            }
        }

        return cells.Count / 2;
    }

    private static void InsertAllChecked(TableLeafPage page, IEnumerable<TableLeafCell> cells)
    {
        foreach (var cell in cells)
        {
            var status = page.TryInsert(cell);
            if (status != TableLeafInsertStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to insert cell with key {cell.PrimaryKey} during leaf split: {status}.");
            }
        }
    }

    private static void AssertInternalInsert(TableInternalInsertStatus status, long key)
    {
        if (status != TableInternalInsertStatus.Success)
        {
            throw new InvalidOperationException(
                $"Failed to insert divider key {key} into internal page during split: {status}.");
        }
    }

    // ── Overflow helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="payload"/> across one or more freshly allocated overflow pages
    /// and returns the page ID of the first page in the chain.
    /// </summary>
    private uint SpillToOverflow(byte[] payload)
    {
        var capacity = OverflowPage.DataCapacity;
        int pageCount = (payload.Length + capacity - 1) / capacity;

        // Allocate all pages up-front so partial failures don't leave the pager in an
        // inconsistent state (a later write failure will still surface as an exception
        // but the pages will be properly linked).
        var pageIds = new uint[pageCount];
        for (int i = 0; i < pageCount; i++)
            pageIds[i] = _pager.AllocatePage(PageType.Overflow);

        for (int i = 0; i < pageCount; i++)
        {
            var pageBuffer = _pager.ReadPage(pageIds[i]);
            int offset = i * capacity;
            int length = Math.Min(capacity, payload.Length - offset);
            payload.AsSpan(offset, length).CopyTo(OverflowPage.DataSpan(pageBuffer));
            OverflowPage.SetNextPageId(pageBuffer, i + 1 < pageCount ? pageIds[i + 1] : 0u);
            _pager.WritePage(pageBuffer);
        }

        return pageIds[0];
    }

    /// <summary>
    /// Reads the payload stored across the overflow chain starting at <paramref name="firstPageId"/>.
    /// </summary>
    private byte[] ReadFromOverflow(uint firstPageId, int totalSize)
    {
        var result = new byte[totalSize];
        int written = 0;
        var currentPageId = firstPageId;
        int capacity = OverflowPage.DataCapacity;
        int maxPages = (totalSize + capacity - 1) / capacity;
        int visitedPages = 0;

        while (currentPageId != 0)
        {
            if (++visitedPages > maxPages)
                throw new InvalidOperationException("Overflow chain is longer than expected; possible corruption.");

            var page = _pager.ReadPage(currentPageId);

            if (page.ReadHeader().PageType != PageType.Overflow)
                throw new InvalidOperationException($"Page {currentPageId} is not an overflow page; possible corruption.");

            int toCopy = Math.Min(capacity, totalSize - written);
            OverflowPage.DataReadOnlySpan(page)[..toCopy].CopyTo(result.AsSpan(written));
            written += toCopy;
            currentPageId = OverflowPage.ReadNextPageId(page);
        }

        if (written != totalSize)
            throw new InvalidOperationException($"Overflow chain returned {written} bytes but {totalSize} were expected; possible corruption.");

        return result;
    }

    /// <summary>
    /// Walks and releases every page in an overflow chain back to the free list.
    /// </summary>
    private void ReleaseOverflowChain(uint firstPageId, int totalSize)
    {
        var currentPageId = firstPageId;
        int capacity = OverflowPage.DataCapacity;
        int maxPages = (totalSize + capacity - 1) / capacity;
        int visited = 0;

        while (currentPageId != 0)
        {
            if (++visited > maxPages)
                break; // Avoid runaway loop on corrupt chains.

            var page = _pager.ReadPage(currentPageId);
            var nextPageId = OverflowPage.ReadNextPageId(page);
            _pager.ReleasePage(currentPageId);
            currentPageId = nextPageId;
        }
    }

    /// <summary>
    /// Decodes the 8-byte payload of an overflow pointer cell into (totalSize, firstPageId).
    /// </summary>
    private static (int TotalSize, uint FirstPageId) ReadOverflowPointer(TableLeafCell pointerCell)
    {
        var totalSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(pointerCell.Payload[..sizeof(uint)]);
        var firstPageId = BinaryPrimitives.ReadUInt32LittleEndian(pointerCell.Payload[sizeof(uint)..]);
        return (totalSize, firstPageId);
    }

    /// <summary>
    /// Builds an overflow pointer cell with the given key, total payload size, and first
    /// overflow page ID.
    /// </summary>
    private static TableLeafCell BuildOverflowPointerCell(long primaryKey, int totalSize, uint firstPageId)
    {
        var payload = new byte[BTreePageLayout.OverflowPointerPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, sizeof(uint)), (uint)totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(sizeof(uint), sizeof(uint)), firstPageId);
        return new TableLeafCell(primaryKey, payload) { IsOverflowPointer = true };
    }

    /// <summary>
    /// If <paramref name="raw"/> is an overflow pointer cell, reads and returns the full
    /// payload from the overflow chain. Otherwise returns the cell unchanged.
    /// </summary>
    private TableLeafCell MaterializeCell(TableLeafCell raw)
    {
        if (!raw.IsOverflowPointer)
            return raw;

        var (totalSize, firstPageId) = ReadOverflowPointer(raw);
        return new TableLeafCell(raw.PrimaryKey, ReadFromOverflow(firstPageId, totalSize));
    }
}
