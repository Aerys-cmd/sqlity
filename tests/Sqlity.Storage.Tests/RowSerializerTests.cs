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
}
