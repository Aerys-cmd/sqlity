# ADO.NET provider

The ADO.NET provider wraps the `QueryEngine` and exposes standard .NET database APIs without
re-implementing any storage or execution logic.

## Classes

| Class | Base class | Purpose |
|---|---|---|
| `SqlityConnection` | `DbConnection` | Opens a `.sqlity` file; owns the `QueryEngine` lifetime |
| `SqlityCommand` | `DbCommand` | Holds a SQL string; delegates execution to the connection's engine |
| `SqlityDataReader` | `DbDataReader` | Iterates rows from a `QueryExecutionResult` |
| `SqlityParameter` | `DbParameter` | Stub — no parameterised query support yet |
| `SqlityParameterCollection` | `DbParameterCollection` | Stub — required by `DbCommand` |
| `SqlityTransaction` | `DbTransaction` | Stub — `Commit`/`Rollback` throw `NotSupportedException` (see Roadmap §3) |

## Connection string

```
Data Source=path/to/file.sqlity
```

A bare file path is also accepted as a convenience.

## Usage

```csharp
using Sqlity.Ado;

using var conn = new SqlityConnection("Data Source=mydb.sqlity");
conn.Open();

// DDL
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);";
    cmd.ExecuteNonQuery();
}

// INSERT
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "INSERT INTO users VALUES (1, 'Ada');";
    cmd.ExecuteNonQuery();
}

// SELECT
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT id, name FROM users;";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetString(1)}");
}

// Scalar
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT id FROM users WHERE id = 1;";
    var id = cmd.ExecuteScalar(); // returns 1L
}
```

## How it maps to the query engine

`SqlityConnection.Open()` creates a `QueryEngine(DataSource)` which opens or creates the file.
Every `ExecuteNonQuery`, `ExecuteScalar`, and `ExecuteReader` call on `SqlityCommand` delegates
directly to `QueryEngine.Execute(sql)`. The returned `QueryExecutionResult` is wrapped in
`SqlityDataReader` without re-executing or copying data.

`SqlityConnection.Close()` (and `Dispose()`) disposes the `QueryEngine`, which releases the
file handle.

## DbDataReader surface

`SqlityDataReader` exposes the full `DbDataReader` contract:

- `Read()` / `HasRows` / `IsClosed`
- `FieldCount` / `GetName(int)` / `GetOrdinal(string)` — ordinal lookup is case-insensitive
- `GetValue(int)` — returns `DBNull.Value` for null cells
- `IsDBNull(int)`
- `GetString`, `GetInt64`, `GetInt32`, `GetBoolean`, `GetByte`, `GetInt16`, `GetFloat`,
  `GetDouble`, `GetDecimal`, `GetBytes`, `GetChars`
- Indexers `reader[int]` and `reader[string]`
- `RecordsAffected` — populated for `INSERT`/`UPDATE`/`DELETE` results

## Current limitations

- No parameterised queries (`@param` syntax is not parsed by the engine yet).
- No transaction support — `BeginTransaction()` returns a stub that throws on `Commit`/`Rollback`.
- `GetFieldType` falls back to inspecting the first row's CLR value type; it returns
  `typeof(object)` for empty result sets.
