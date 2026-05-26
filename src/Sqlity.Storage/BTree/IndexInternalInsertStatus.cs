namespace Sqlity.Storage.BTree;

internal enum IndexInternalInsertStatus
{
    Success,
    DuplicateKey,
    PageFull
}
