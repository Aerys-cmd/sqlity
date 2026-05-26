using Sqlity.Storage.BTree;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class IndexKeyEncoderTests
{
    // Each test uses a schema with an int64 PK at ordinal 0, plus the test column at ordinal 1.
    private static TableSchema MakeSchema(ColumnDefinition col)
    {
        return new TableSchema("t",
            new[] { new ColumnDefinition("id", ColumnType.Int64), col },
            primaryKeyOrdinal: 0);
    }

    private static byte[] Encode(TableSchema schema, int ordinal, object? value, long? pk = null) =>
        IndexKeyEncoder.Encode(schema.Columns, new[] { ordinal }, BuildRow(schema, ordinal, value), pk);

    // Build a full-row array with only the given ordinal set.
    private static object?[] BuildRow(TableSchema schema, int ordinal, object? value)
    {
        var row = new object?[schema.Columns.Count];
        row[ordinal] = value;
        return row;
    }

    // ── Int64 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Int64_negative_sorts_before_zero_before_positive()
    {
        var schema = MakeSchema(new ColumnDefinition("x", ColumnType.Int64));
        var negKey = Encode(schema, 1, -1L);
        var zeroKey = Encode(schema, 1, 0L);
        var posKey = Encode(schema, 1, 1L);

        Assert.True(negKey.AsSpan().SequenceCompareTo(zeroKey) < 0);
        Assert.True(zeroKey.AsSpan().SequenceCompareTo(posKey) < 0);
    }

    [Fact]
    public void Int64_min_value_sorts_first()
    {
        var schema = MakeSchema(new ColumnDefinition("x", ColumnType.Int64));
        var minKey = Encode(schema, 1, long.MinValue);
        var maxKey = Encode(schema, 1, long.MaxValue);

        Assert.True(minKey.AsSpan().SequenceCompareTo(maxKey) < 0);
    }

    // ── String ─────────────────────────────────────────────────────────────────

    [Fact]
    public void String_sorts_lexicographically()
    {
        var schema = MakeSchema(new ColumnDefinition("name", ColumnType.String));
        var aKey = Encode(schema, 1, "Alice");
        var bKey = Encode(schema, 1, "Bob");

        Assert.True(aKey.AsSpan().SequenceCompareTo(bKey) < 0);
    }

    [Fact]
    public void String_prefix_sorts_before_longer_string()
    {
        var schema = MakeSchema(new ColumnDefinition("name", ColumnType.String));
        var prefixKey = Encode(schema, 1, "A");
        var longerKey = Encode(schema, 1, "AB");

        Assert.True(prefixKey.AsSpan().SequenceCompareTo(longerKey) < 0);
    }

    // ── Boolean ────────────────────────────────────────────────────────────────

    [Fact]
    public void Boolean_false_sorts_before_true()
    {
        var schema = MakeSchema(new ColumnDefinition("flag", ColumnType.Boolean));
        var falseKey = Encode(schema, 1, false);
        var trueKey = Encode(schema, 1, true);

        Assert.True(falseKey.AsSpan().SequenceCompareTo(trueKey) < 0);
    }

    // ── Null ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Null_sorts_before_non_null_int64()
    {
        var schema = MakeSchema(new ColumnDefinition("x", ColumnType.Int64, IsNullable: true));
        var nullKey = Encode(schema, 1, null);
        var zeroKey = Encode(schema, 1, 0L);

        Assert.True(nullKey.AsSpan().SequenceCompareTo(zeroKey) < 0);
    }

    // ── Multi-column ───────────────────────────────────────────────────────────

    [Fact]
    public void MultiColumn_first_column_dominates_ordering()
    {
        // Schema: id(0), a(1), b(2)
        var schema = new TableSchema("t",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("a", ColumnType.Int64),
                new ColumnDefinition("b", ColumnType.Int64)
            },
            primaryKeyOrdinal: 0);
        var ordinals = new[] { 1, 2 };

        var row1 = new object?[] { null, 1L, 999L };
        var row2 = new object?[] { null, 2L, 0L };
        var k1 = IndexKeyEncoder.Encode(schema.Columns, ordinals, row1);
        var k2 = IndexKeyEncoder.Encode(schema.Columns, ordinals, row2);

        Assert.True(k1.AsSpan().SequenceCompareTo(k2) < 0);
    }

    [Fact]
    public void MultiColumn_second_column_breaks_tie()
    {
        var schema = new TableSchema("t",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("a", ColumnType.Int64),
                new ColumnDefinition("b", ColumnType.Int64)
            },
            primaryKeyOrdinal: 0);
        var ordinals = new[] { 1, 2 };

        var row1 = new object?[] { null, 1L, 1L };
        var row2 = new object?[] { null, 1L, 2L };
        var k1 = IndexKeyEncoder.Encode(schema.Columns, ordinals, row1);
        var k2 = IndexKeyEncoder.Encode(schema.Columns, ordinals, row2);

        Assert.True(k1.AsSpan().SequenceCompareTo(k2) < 0);
    }

    // ── PK suffix ──────────────────────────────────────────────────────────────

    [Fact]
    public void PK_suffix_appended_to_non_unique_key()
    {
        var schema = MakeSchema(new ColumnDefinition("name", ColumnType.String));
        var withPk = Encode(schema, 1, "Alice", pk: 42L);
        var withoutPk = Encode(schema, 1, "Alice");

        // key with PK is longer but starts with the same prefix
        Assert.True(withPk.Length > withoutPk.Length);
        Assert.Equal(withoutPk, withPk[..withoutPk.Length]);
    }
}
