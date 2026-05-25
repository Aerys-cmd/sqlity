using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Tests;

public sealed class TableInternalPageTests
{
    // ── Construction ─────────────────────────────────────────────────────────

    [Fact]
    public void Create_sets_leftmost_child_page_id()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 42);

        Assert.Equal(42u, page.LeftmostChildPageId);
        Assert.Equal(0, page.EntryCount);
    }

    [Fact]
    public void Constructor_rejects_non_internal_page()
    {
        var leafBuffer = PageBuffer.Create(1, PageType.TableLeaf);

        Assert.Throws<InvalidOperationException>(() => new TableInternalPage(leafBuffer));
    }

    // ── TryInsert / ReadAllCells ─────────────────────────────────────────────

    [Fact]
    public void TryInsert_succeeds_and_entries_are_sorted()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 10);

        Assert.Equal(TableInternalInsertStatus.Success, page.TryInsert(new TableInternalCell(30, 4)));
        Assert.Equal(TableInternalInsertStatus.Success, page.TryInsert(new TableInternalCell(10, 2)));
        Assert.Equal(TableInternalInsertStatus.Success, page.TryInsert(new TableInternalCell(20, 3)));

        var cells = page.ReadAllCells();
        Assert.Equal(3, cells.Count);
        Assert.Equal(10, cells[0].DividerKey);
        Assert.Equal(2u, cells[0].RightChildPageId);
        Assert.Equal(20, cells[1].DividerKey);
        Assert.Equal(3u, cells[1].RightChildPageId);
        Assert.Equal(30, cells[2].DividerKey);
        Assert.Equal(4u, cells[2].RightChildPageId);
    }

    [Fact]
    public void TryInsert_returns_duplicate_for_same_key()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 5);
        page.TryInsert(new TableInternalCell(100, 2));

        var status = page.TryInsert(new TableInternalCell(100, 3));

        Assert.Equal(TableInternalInsertStatus.DuplicateKey, status);
        Assert.Equal(1, page.EntryCount);
    }

    [Fact]
    public void TryInsert_returns_page_full_when_no_space()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 0);
        TableInternalInsertStatus status;

        // Fill the page until it reports full.
        var inserted = 0;
        for (long key = 1; key <= 10_000; key++)
        {
            status = page.TryInsert(new TableInternalCell(key, (uint)key + 1));
            if (status == TableInternalInsertStatus.PageFull) break;
            inserted++;
        }

        // Must have rejected at least one.
        status = page.TryInsert(new TableInternalCell(99_999, 9));
        Assert.Equal(TableInternalInsertStatus.PageFull, status);
        Assert.True(inserted > 0);
    }

    // ── FindChildPageId ──────────────────────────────────────────────────────

    [Fact]
    public void FindChildPageId_returns_leftmost_child_when_key_is_below_all_dividers()
    {
        // Layout: leftmost=1, entries: (10, 2), (20, 3), (30, 4)
        // Key 5 < 10 → leftmost child (page 1)
        var page = TableInternalPage.Create(1, leftmostChildPageId: 1);
        page.TryInsert(new TableInternalCell(10, 2));
        page.TryInsert(new TableInternalCell(20, 3));
        page.TryInsert(new TableInternalCell(30, 4));

        Assert.Equal(1u, page.FindChildPageId(5));
    }

    [Fact]
    public void FindChildPageId_returns_exact_match_right_child()
    {
        // Key 10 >= divider 10 → right child page 2
        var page = TableInternalPage.Create(1, leftmostChildPageId: 1);
        page.TryInsert(new TableInternalCell(10, 2));
        page.TryInsert(new TableInternalCell(20, 3));
        page.TryInsert(new TableInternalCell(30, 4));

        Assert.Equal(2u, page.FindChildPageId(10));
    }

    [Fact]
    public void FindChildPageId_returns_correct_child_for_middle_range()
    {
        // Key 15 falls between 10 and 20 → right child of divider 10 (page 2)
        var page = TableInternalPage.Create(1, leftmostChildPageId: 1);
        page.TryInsert(new TableInternalCell(10, 2));
        page.TryInsert(new TableInternalCell(20, 3));
        page.TryInsert(new TableInternalCell(30, 4));

        Assert.Equal(2u, page.FindChildPageId(15));
    }

    [Fact]
    public void FindChildPageId_returns_rightmost_child_when_key_exceeds_all_dividers()
    {
        // Key 99 > 30 → right child of divider 30 (page 4)
        var page = TableInternalPage.Create(1, leftmostChildPageId: 1);
        page.TryInsert(new TableInternalCell(10, 2));
        page.TryInsert(new TableInternalCell(20, 3));
        page.TryInsert(new TableInternalCell(30, 4));

        Assert.Equal(4u, page.FindChildPageId(99));
    }

    [Fact]
    public void FindChildPageId_with_single_entry_covers_both_sides()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 10);
        page.TryInsert(new TableInternalCell(50, 20));

        Assert.Equal(10u, page.FindChildPageId(49));
        Assert.Equal(20u, page.FindChildPageId(50));
        Assert.Equal(20u, page.FindChildPageId(51));
    }

    // ── ReadCell ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReadCell_throws_for_out_of_range_index()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => page.ReadCell(0));
    }

    [Fact]
    public void ReadCell_returns_correct_entry()
    {
        var page = TableInternalPage.Create(1, leftmostChildPageId: 5);
        page.TryInsert(new TableInternalCell(77, 99));

        var cell = page.ReadCell(0);

        Assert.Equal(77, cell.DividerKey);
        Assert.Equal(99u, cell.RightChildPageId);
    }

    // ── Round-trip via PageBuffer ────────────────────────────────────────────

    [Fact]
    public void LeftmostChildPageId_round_trips_through_page_header()
    {
        var page = TableInternalPage.Create(5, leftmostChildPageId: 1234);
        page.TryInsert(new TableInternalCell(42, 7));

        // Re-read from the same buffer.
        var reloaded = new TableInternalPage(page.Page);

        Assert.Equal(1234u, reloaded.LeftmostChildPageId);
        Assert.Equal(1, reloaded.EntryCount);
        Assert.Equal(42, reloaded.ReadCell(0).DividerKey);
    }
}
