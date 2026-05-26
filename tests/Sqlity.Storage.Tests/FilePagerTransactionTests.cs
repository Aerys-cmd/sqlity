using Sqlity.Storage.IO;

namespace Sqlity.Storage.Tests;

public sealed class FilePagerTransactionTests
{
    [Fact]
    public void Commit_persists_changes_and_removes_journal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var journalPath = path + ".journal";

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            pager.BeginTransaction();
            Assert.True(pager.InTransaction);
            Assert.True(File.Exists(journalPath));

            _ = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);
            pager.Commit();

            Assert.False(pager.InTransaction);
            Assert.False(File.Exists(journalPath));

            var header = pager.ReadDatabaseHeader();
            Assert.Equal(2u, header.PageCount); // header + 1 allocated page
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
    }

    [Fact]
    public void Rollback_restores_original_state_and_removes_journal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var journalPath = path + ".journal";

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            var headerBefore = pager.ReadDatabaseHeader();

            pager.BeginTransaction();
            _ = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);
            _ = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);

            pager.Rollback();

            Assert.False(pager.InTransaction);
            Assert.False(File.Exists(journalPath));

            var headerAfter = pager.ReadDatabaseHeader();
            Assert.Equal(headerBefore.PageCount, headerAfter.PageCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
    }

    [Fact]
    public void RecoverIfNeeded_rolls_back_stale_journal_on_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var journalPath = path + ".journal";

        try
        {
            // First session: begin transaction, write some pages, but crash (no commit)
            uint originalPageCount;
            {
                using var pager = new FilePager(path);
                pager.InitializeNew();
                originalPageCount = pager.ReadDatabaseHeader().PageCount;

                pager.BeginTransaction();
                _ = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);
                _ = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);
                // Simulate crash: dispose without Commit or Rollback
                // Journal file remains on disk
            }

            // Journal must still exist after the crash
            Assert.True(File.Exists(journalPath));

            // Second session: open should auto-recover
            using var recoveredPager = new FilePager(path);
            recoveredPager.RecoverIfNeeded();

            Assert.False(File.Exists(journalPath));
            var recoveredHeader = recoveredPager.ReadDatabaseHeader();
            Assert.Equal(originalPageCount, recoveredHeader.PageCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
    }

    [Fact]
    public void BeginTransaction_throws_when_transaction_already_active()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var journalPath = path + ".journal";

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();
            pager.BeginTransaction();

            Assert.Throws<InvalidOperationException>(() => pager.BeginTransaction());

            pager.Rollback();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
    }

    [Fact]
    public void Commit_throws_when_no_active_transaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            Assert.Throws<InvalidOperationException>(() => pager.Commit());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Rollback_throws_when_no_active_transaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            Assert.Throws<InvalidOperationException>(() => pager.Rollback());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
