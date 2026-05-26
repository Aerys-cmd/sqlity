namespace Sqlity.Storage.Pages;

public enum PageType : byte
{
    Unknown = 0,
    TableLeaf = 1,
    TableInternal = 2,
    FreeList = 3,
    Overflow = 4,
    IndexLeaf = 5,
    IndexInternal = 6
}
