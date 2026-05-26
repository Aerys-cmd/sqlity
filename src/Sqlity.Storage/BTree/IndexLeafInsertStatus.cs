namespace Sqlity.Storage.BTree;

internal enum IndexLeafInsertStatus
{
    Success,
    DuplicateKey,
    PageFull
}

internal enum IndexLeafDeleteStatus
{
    Success,
    NotFound
}
