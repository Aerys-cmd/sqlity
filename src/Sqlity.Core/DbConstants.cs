namespace Sqlity.Core;

public static class DbConstants
{
    public const int PageSize = 4096;
    public const uint FormatVersion = 1;
    public const uint HeaderPageNumber = 0;

    public static ReadOnlySpan<byte> Magic => "SQLITYDB"u8;
}
