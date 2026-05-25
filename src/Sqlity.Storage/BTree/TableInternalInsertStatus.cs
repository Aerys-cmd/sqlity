namespace Sqlity.Storage.BTree;

public enum TableInternalInsertStatus
{
    Success = 0,
    DuplicateKey = 1,
    PageFull = 2
}
