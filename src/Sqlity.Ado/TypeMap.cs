using System.Data;
using Sqlity.Storage.Rows;

namespace Sqlity.Ado;

internal static class TypeMap
{
    public static Type ToClrType(ColumnType ct) => ct switch
    {
        ColumnType.Null => typeof(object),
        ColumnType.Int64 => typeof(long),
        ColumnType.String => typeof(string),
        ColumnType.Blob => typeof(byte[]),
        ColumnType.Boolean => typeof(bool),
        ColumnType.Float64 => typeof(double),
        ColumnType.Date => typeof(DateOnly),
        ColumnType.DateTime => typeof(DateTime),
        _ => typeof(object),
    };

    public static DbType ToDbType(ColumnType ct) => ct switch
    {
        ColumnType.Null => DbType.Object,
        ColumnType.Int64 => DbType.Int64,
        ColumnType.String => DbType.String,
        ColumnType.Blob => DbType.Binary,
        ColumnType.Boolean => DbType.Boolean,
        ColumnType.Float64 => DbType.Double,
        ColumnType.Date => DbType.Date,
        ColumnType.DateTime => DbType.DateTime,
        _ => DbType.Object
    };
}
