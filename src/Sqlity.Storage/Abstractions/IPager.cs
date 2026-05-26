using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Abstractions;

public interface IPager : IDisposable
{
    void InitializeNew();
    DatabaseHeader ReadDatabaseHeader();
    void WriteDatabaseHeader(in DatabaseHeader header);
    PageBuffer ReadPage(uint pageNumber);
    void WritePage(PageBuffer page);
    uint AllocatePage(PageType pageType);
    void ReleasePage(uint pageNumber);
    bool InTransaction { get; }
    void BeginTransaction();
    void Commit();
    void Rollback();
}
