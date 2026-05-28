using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace Sqlity.EFCore;

public sealed class SqlityTypeMappingSource(
    TypeMappingSourceDependencies dependencies,
    RelationalTypeMappingSourceDependencies relationalDependencies)
    : RelationalTypeMappingSource(dependencies, relationalDependencies)
{
    private static readonly Dictionary<string, RelationalTypeMapping> StoreTypeMappings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["STRING"]   = new StringTypeMapping("STRING",   DbType.String),
            ["INT64"]    = new LongTypeMapping("INT64",      DbType.Int64),
            ["BOOLEAN"]  = new BoolTypeMapping("BOOLEAN",    DbType.Boolean),
            ["REAL"]     = new DoubleTypeMapping("REAL",     DbType.Double),
            ["DATETIME"] = new DateTimeTypeMapping("DATETIME", DbType.DateTime),
            // DATE maps to DateOnly (not DateTime) — matches Sqlity's internal TypeMap
            ["DATE"]     = new DateOnlyTypeMapping("DATE"),
        };

    private static readonly Dictionary<Type, RelationalTypeMapping> ClrTypeMappings = new()
    {
        [typeof(string)]   = new StringTypeMapping("STRING",   DbType.String),
        [typeof(long)]     = new LongTypeMapping("INT64",      DbType.Int64),
        [typeof(int)]      = new IntTypeMapping("INT64",       DbType.Int32),
        [typeof(short)]    = new ShortTypeMapping("INT64",     DbType.Int16),
        [typeof(byte)]     = new ByteTypeMapping("INT64",      DbType.Byte),
        [typeof(bool)]     = new BoolTypeMapping("BOOLEAN",    DbType.Boolean),
        [typeof(double)]   = new DoubleTypeMapping("REAL",     DbType.Double),
        [typeof(float)]    = new FloatTypeMapping("REAL",      DbType.Single),
        [typeof(decimal)]  = new DecimalTypeMapping("REAL",    DbType.Decimal),
        [typeof(DateTime)] = new DateTimeTypeMapping("DATETIME", DbType.DateTime),
        [typeof(DateOnly)] = new DateOnlyTypeMapping("DATE"),
    };

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var storeName = mappingInfo.StoreTypeName;
        var clrType = mappingInfo.ClrType;

        // When both store name and CLR type are known, find the mapping that satisfies both.
        // E.g. an int property with HasColumnType("INT64") must not return a LongTypeMapping.
        if (storeName is not null && clrType is not null)
        {
            if (ClrTypeMappings.TryGetValue(clrType, out var clrM) &&
                string.Equals(clrM.StoreType, storeName, StringComparison.OrdinalIgnoreCase))
                return clrM;
        }

        if (storeName is not null && StoreTypeMappings.TryGetValue(storeName, out var storeMapping))
            return storeMapping;

        if (clrType is not null && ClrTypeMappings.TryGetValue(clrType, out var clrMapping))
            return clrMapping;

        return base.FindMapping(mappingInfo);
    }
}
