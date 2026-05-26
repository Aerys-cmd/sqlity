# Sqlity

Sqlity is an educational SQLite-like embedded database engine written in C# for .NET 10. The project is intentionally scoped for learning and public architecture exploration, not for production use or SQLite compatibility.

## Goals

- Learn database internals by building them incrementally.
- Keep the implementation technically correct and readable.
- Show serious systems design without hiding core ideas behind unnecessary abstractions.
- Grow from storage primitives to SQL execution, ADO.NET, and EF Core integration.

## Current milestone

The repository now contains a storage engine, an executable SQL layer, secondary index support with a rule-based query planner, a complete ADO.NET provider, and full transaction support with crash recovery:

- a single-file database format
- a fixed 4096-byte page model
- binary file and page headers
- row serialization primitives with `NULL` value support
- free-page list primitives; emptied B+ tree leaf pages are recycled back to the free list
- a persisted table catalog and schema serializer (version 2 with per-column nullable flags)
- single-page table storage with ordered primary-key insertion
- slotted-page compaction on delete (correct pointer array and cell content compaction)
- in-place and resize-safe row updates
- SQL execution for `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, `UPDATE`, `CREATE INDEX`, and `CREATE UNIQUE INDEX`
- nullable columns (`NOT NULL` constraint), `NULL` literals, and `IS NULL` / `IS NOT NULL` in `WHERE`
- `INNER JOIN` and `LEFT JOIN` with compound `WHERE` expressions
- secondary B+ trees with sort-preserving key encoding for all column types
- persisted index catalog (`__sqlity_indexes`) that survives reopen
- automatic index maintenance on `INSERT`, `DELETE`, and `UPDATE`
- rule-based logical/physical query planner: equality predicates on indexed columns produce an index seek; unmatched predicates become a post-filter
- `CREATE [UNIQUE] INDEX` with duplicate-key enforcement on unique indexes
- full ADO.NET provider: `SqlityConnection`, `SqlityCommand`, `SqlityDataReader`, `SqlityParameter`
- `BEGIN` / `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` transaction boundaries
- rollback journal: every write is journaled before it happens; a stale journal on reopen triggers automatic crash recovery
- auto-commit for statements executed outside an explicit `BEGIN`
- multi-statement batch execution: multiple `;`-separated statements in a single `Execute` call
- storage, query, CLI, and ADO.NET test coverage

## Repository layout

```text
src/
  Sqlity.Core      Shared constants and future cross-layer primitives
  Sqlity.Storage   File format, catalog persistence, row encoding, pager, and single-page table storage
  Sqlity.Query     MVP SQL parsing, binding, and execution
  Sqlity.Ado       ADO.NET provider (DbConnection, DbCommand, DbDataReader)
  Sqlity.EFCore    Planned EF Core provider
samples/
  Sqlity.Cli       Tiny console app for opening a `.sqlity` file and executing one SQL statement
tests/
  Sqlity.Ado.Tests
  Sqlity.Cli.Tests
  Sqlity.Query.Tests
  Sqlity.Storage.Tests
docs/
  architecture.md
  storage-engine.md
  query-engine.md
  ado-provider.md
  efcore-provider.md
  next-roadmap.md
```

## Why page-based storage first?

Databases almost always converge on page-based I/O because disks and operating systems work best with block-sized reads and writes. A page gives the engine a stable unit for caching, addressing, serialization, crash recovery, and B-tree navigation.

Sqlity uses:

- page 0 as the database header page
- pages 1..N as regular storage pages
- fixed 4096-byte pages
- little-endian binary encoding
- slotted-page ideas for B-tree-friendly layouts

## Initial storage decisions

1. **Database header page** stores file-wide metadata such as page size, page count, the system-catalog root page id, and the free-list head.
2. **Regular pages** start with a small generic page header and keep cell payloads at the end of the page so a slot array can grow from the front.
3. **Rows** are serialized manually with schema-bound type tags and length-prefixed payloads.
4. **The system catalog** is stored as a normal table leaf page so schemas survive reopen without inventing a second metadata format.
5. **Free pages** are linked together as a singly linked list for the MVP.
6. **Table leaf pages** already perform ordered primary-key inserts directly inside the slotted-page layout.
7. **B-trees** are the long-term table storage structure because they keep point lookups and ordered scans efficient while fitting the page model cleanly.

## Incremental roadmap

1. Add root-page search and page split behavior for the B-tree path.
2. ~~Add delete/compaction behavior for table leaf pages.~~ ✅ Done — `DELETE` and `UPDATE` are fully implemented with correct slotted-page compaction.
3. ~~Expose the engine through an ADO.NET provider.~~ ✅ Done — `SqlityConnection`, `SqlityCommand`, `SqlityDataReader`, and `SqlityParameter` are implemented.
4. Add an EF Core provider after the ADO.NET provider is stable.
5. Add transactions, then WAL, once the base storage design is solid.

## Documentation

- `docs/architecture.md` explains how the layers fit together.
- `docs/storage-engine.md` explains the page model, on-disk layout, and roadmap in detail.
- `docs/query-engine.md` explains the current SQL surface and its deliberate MVP limits.
- `docs/ado-provider.md` explains the ADO.NET provider API and how it wraps the query engine.
- `docs/transactions.md` explains the rollback journal, crash-recovery invariants, and transaction usage.
- `docs/next-roadmap.md` captures the next concrete milestones.

## Creating your own database

### Via ADO.NET (recommended)

The ADO.NET provider is the standard way to interact with Sqlity from .NET code. Use
`SqlityConnection` with a `Data Source=` connection string, then work with the familiar
`DbCommand` and `DbDataReader` API.

```csharp
using System.Data.Common;
using Sqlity.Ado;

using var conn = new SqlityConnection("Data Source=demo.sqlity");
conn.Open();

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        CREATE TABLE users (
            id    INT64   PRIMARY KEY,
            name  STRING,
            score INT64   NOT NULL,
            active BOOLEAN
        );
        """;
    cmd.ExecuteNonQuery();
}

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "INSERT INTO users VALUES (1, 'Ada', TRUE);";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO users VALUES (2, 'Linus', FALSE);";
    cmd.ExecuteNonQuery();
}

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT id, name FROM users;";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetString(1)}");
}
```

### Via QueryEngine (lower-level)

You can also work directly with `QueryEngine`. Passing a file path creates the database if it
does not exist yet, then subsequent `Execute(...)` calls run against the same file.

```csharp
using Sqlity.Query;

using var engine = new QueryEngine("demo.sqlity");

engine.Execute("""
    CREATE TABLE users (
        id INT64 PRIMARY KEY,
        name STRING,
        score INT64 NOT NULL,
        is_active BOOLEAN
    );
    """);

engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");
engine.Execute("INSERT INTO users VALUES (2, 'Linus', FALSE);");

var result = engine.Execute("SELECT id, name FROM users WHERE id = 2;");

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]}, {row[1]}");
}

engine.Execute("UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;");
engine.Execute("DELETE FROM users WHERE id = 2;");
```

Current limits to keep in mind:

- supported statements are `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, `UPDATE`, `CREATE INDEX`, `CREATE UNIQUE INDEX`, `BEGIN` / `BEGIN TRANSACTION`, `COMMIT`, and `ROLLBACK`; multiple statements can be batched in a single call
- `WHERE` supports any column with full `AND`/`OR` composition and `IS NULL` / `IS NOT NULL`; equality predicates on indexed columns use a secondary B+ tree seek; primary-key equality uses a primary B+ tree point lookup; unmatched predicates apply as a post-filter
- no aggregates, no `ORDER BY`, no subqueries

That file path is the database. If `my-db.sqlity` does not exist, Sqlity creates it; if it exists, Sqlity reopens it.

**How you can create your own DB and query it today:**

```csharp
using Sqlity.Query;

using var engine = new QueryEngine("my-db.sqlity");
engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);");
engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");

var result = engine.Execute("SELECT id, name FROM users WHERE id = 1;");
```

### Tiny CLI workflow

There is also a small runnable console app in `samples/Sqlity.Cli` for the same workflow:

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);"
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "INSERT INTO users VALUES (1, 'Ada', TRUE);"
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "SELECT id, name FROM users WHERE id = 1;"
```

You can also pipe SQL through standard input:

```bash
echo "SELECT id, name FROM users WHERE id = 1;" | dotnet run --project samples/Sqlity.Cli -- demo.sqlity
```

For a larger set of executable command/output examples that is generated from tests, see `samples/Sqlity.Cli/README.md`.

## Running tests

```bash
dotnet test Sqlity.slnx
```
