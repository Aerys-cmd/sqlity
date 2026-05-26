using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class RowSerializerTests
{
    [Fact]
    public void RowSerializer_round_trips_a_schema_bound_row()
    {
        var schema = new TableSchema(
            "users",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("name", ColumnType.String),
                new ColumnDefinition("is_active", ColumnType.Boolean),
                new ColumnDefinition("avatar", ColumnType.Blob)
            },
            primaryKeyOrdinal: 0);

        var values = new object?[]
        {
            123L,
            "Ada",
            true,
            new byte[] { 0x10, 0x20, 0x30 }
        };

        var serializer = new RowSerializer();
        var buffer = new byte[serializer.GetRequiredSize(schema, values)];

        var bytesWritten = serializer.Write(schema, values, buffer);
        var roundTripped = serializer.Read(schema, buffer.AsSpan(0, bytesWritten));

        Assert.Equal(values[0], roundTripped[0]);
        Assert.Equal(values[1], roundTripped[1]);
        Assert.Equal(values[2], roundTripped[2]);
        Assert.Equal((byte[])values[3]!, (byte[])roundTripped[3]!);
    }

    [Fact]
    public void TableSchema_requires_an_int64_primary_key_for_mvp()
    {
        Assert.Throws<NotSupportedException>(
            () => new TableSchema(
                "users",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.String)
                },
                primaryKeyOrdinal: 0));
    }

    // ── NULL support ──────────────────────────────────────────────────────────

    [Fact]
    public void RowSerializer_round_trips_null_in_nullable_column()
    {
        var schema = new TableSchema(
            "users",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("name", ColumnType.String, IsNullable: true)
            },
            primaryKeyOrdinal: 0);

        var values = new object?[] { 1L, null };
        var serializer = new RowSerializer();
        var buffer = new byte[serializer.GetRequiredSize(schema, values)];

        var bytesWritten = serializer.Write(schema, values, buffer);
        var roundTripped = serializer.Read(schema, buffer.AsSpan(0, bytesWritten));

        Assert.Equal(1L, roundTripped[0]);
        Assert.Null(roundTripped[1]);
    }

    [Fact]
    public void TableSchema_coerces_primary_key_to_non_nullable()
    {
        // PK is always non-nullable regardless of IsNullable flag; coercion is silent.
        var schema = new TableSchema(
            "users",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64, IsNullable: true)
            },
            primaryKeyOrdinal: 0);

        Assert.False(schema.PrimaryKeyColumn.IsNullable);
    }

    [Fact]
    public void TableSchemaSerializer_round_trips_nullable_flags_at_version2()
    {
        // Test that nullable/not-null flags survive a close+reopen of the database.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            var schema = new TableSchema(
                "t",
                new[]
                {
                    new ColumnDefinition("id",    ColumnType.Int64,  IsNullable: false),
                    new ColumnDefinition("label", ColumnType.String, IsNullable: true),
                    new ColumnDefinition("score", ColumnType.Int64,  IsNullable: false)
                },
                primaryKeyOrdinal: 0);

            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(schema);
            }

            using var reopened = StorageEngine.Open(path);
            var roundTripped = reopened.GetTable("t").Schema;

            Assert.Equal(3, roundTripped.Columns.Count);
            Assert.False(roundTripped.Columns[0].IsNullable);
            Assert.True(roundTripped.Columns[1].IsNullable);
            Assert.False(roundTripped.Columns[2].IsNullable);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
