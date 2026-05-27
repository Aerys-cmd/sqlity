namespace Sqlity.Storage.Rows;

public enum ColumnType : byte
{
    Null = 0,
    Int64 = 1,
    String = 2,
    Blob = 3,
    Boolean = 4,
    Float64 = 5
}
