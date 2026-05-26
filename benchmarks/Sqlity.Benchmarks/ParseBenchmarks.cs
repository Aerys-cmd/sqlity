using BenchmarkDotNet.Attributes;
using Sqlity.Query;

namespace Sqlity.Benchmarks;

/// <summary>
/// Benchmarks SQL tokenization and parsing in isolation.
/// No storage engine is involved — each benchmark measures only the
/// SqlTokenizer + SqlParser overhead for a single statement.
/// </summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    private const string InsertSql =
        "INSERT INTO bench VALUES (42, 'Ada Lovelace');";

    private const string SelectSql =
        "SELECT id, name FROM bench WHERE id = 42;";

    private const string CreateTableSql =
        "CREATE TABLE bench (id INT64 PRIMARY KEY, name STRING);";

    [Benchmark]
    public object ParseInsert() =>
        new SqlParser(InsertSql).ParseStatement();

    [Benchmark]
    public object ParseSelect() =>
        new SqlParser(SelectSql).ParseStatement();

    [Benchmark]
    public object ParseCreateTable() =>
        new SqlParser(CreateTableSql).ParseStatement();
}
