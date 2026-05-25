namespace Sqlity.Query.Tests;

public sealed class QueryEngineTests
{
    [Fact]
    public void QueryEngine_executes_create_insert_and_select_with_primary_key_filter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);

            var createResult = engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);");
            var firstInsert = engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");
            var secondInsert = engine.Execute("INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);");
            var selectResult = engine.Execute("SELECT id, name FROM users WHERE id = 2;");

            Assert.Equal(0, createResult.RowsAffected);
            Assert.Equal(1, firstInsert.RowsAffected);
            Assert.Equal(1, secondInsert.RowsAffected);
            Assert.Equal(new[] { "id", "name" }, selectResult.Columns);
            Assert.Collection(
                selectResult.Rows,
                row =>
                {
                    Assert.Equal(2L, row[0]);
                    Assert.Equal("Linus", row[1]);
                });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void QueryEngine_reopens_persisted_catalog_and_supports_projection_and_blob_literals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);");
                engine.Execute("INSERT INTO files VALUES (1, 'spec', X'CAFE');");
            }

            using var reopened = new QueryEngine(path);
            var result = reopened.Execute("SELECT name, payload FROM files WHERE id = 1;");

            Assert.Equal(new[] { "name", "payload" }, result.Columns);
            Assert.Collection(
                result.Rows,
                row =>
                {
                    Assert.Equal("spec", row[0]);
                    Assert.Equal(new byte[] { 0xCA, 0xFE }, row[1]);
                });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
