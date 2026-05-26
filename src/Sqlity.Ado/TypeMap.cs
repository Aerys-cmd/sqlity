using System.Data;
using Sqlity.Storage.Rows;

namespace Sqlity.Ado;

internal static class TypeMap
{
    public static Type ToClrType(ColumnType ct) => ct switch
    {
        ColumnType.Int64   => typeof(long),
        ColumnType.String  => typeof(string),
        ColumnType.Blob    => typeof(byte[]),
        ColumnType.Boolean => typeof(bool),
        _                  => typeof(object),
    };

    public static DbType ToDbType(ColumnType ct) => ct switch
    {
        ColumnType.Int64   => DbType.Int64,
        ColumnType.String  => DbType.String,
        ColumnType.Blob    => DbType.Binary,
        ColumnType.Boolean => DbType.Boolean,
        _                  => DbType.Object,
    };
}
