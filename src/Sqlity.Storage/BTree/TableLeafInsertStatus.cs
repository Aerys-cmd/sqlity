namespace Sqlity.Storage.BTree;

public enum TableLeafInsertStatus
{
    Success = 0,
    DuplicateKey = 1,
    PageFull = 2
}
