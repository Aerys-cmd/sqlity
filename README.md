# Sqlity

Sqlity is an educational SQLite-like embedded database engine written in C# for .NET 10. The project is intentionally scoped for learning and public architecture exploration, not for production use or SQLite compatibility.

## Goals

- Learn database internals by building them incrementally.
- Keep the implementation technically correct and readable.
- Show serious systems design without hiding core ideas behind unnecessary abstractions.
- Grow from storage primitives to SQL execution, ADO.NET, and EF Core integration.

## Current milestone

The repository now contains a single-page storage engine plus the first executable SQL layer:

- a single-file database format
- a fixed 4096-byte page model
- binary file and page headers
- row serialization primitives
- free-page list primitives
- a persisted table catalog and schema serializer
- single-page table storage with ordered primary-key insertion
- MVP SQL execution for `CREATE TABLE`, `INSERT`, and `SELECT`
- storage and query test coverage plus updated architecture documentation

## Repository layout

```text
src/
  Sqlity.Core      Shared constants and future cross-layer primitives
  Sqlity.Storage   File format, catalog persistence, row encoding, pager, and single-page table storage
  Sqlity.Query     MVP SQL parsing, binding, and execution
  Sqlity.Ado       Planned ADO.NET provider
  Sqlity.EFCore    Planned EF Core provider
samples/
  Sqlity.Cli       Tiny console app for opening a `.sqlity` file and executing one SQL statement
tests/
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
2. Add delete/compaction behavior for table leaf pages.
3. Expose the engine through an ADO.NET provider.
4. Add an EF Core provider after the ADO.NET provider is stable.
5. Add transactions, then WAL, once the base storage design is solid.

## Documentation

- `docs/architecture.md` explains how the layers fit together.
- `docs/storage-engine.md` explains the page model, on-disk layout, and roadmap in detail.
- `docs/query-engine.md` explains the current SQL surface and its deliberate MVP limits.
- `docs/next-roadmap.md` captures the next concrete milestones after this query/storage step.

## Creating your own database

The current easiest way to create or reopen a Sqlity database is through `QueryEngine`. Passing a file path creates the database if it does not exist yet, then subsequent `Execute(...)` calls run against the same file.

```csharp
using Sqlity.Query;

using var engine = new QueryEngine("demo.sqlity");

engine.Execute("""
    CREATE TABLE users (
        id INT64 PRIMARY KEY,
        name STRING,
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
```

Current limits to keep in mind:

- supported statements are `CREATE TABLE`, `INSERT`, and `SELECT`
- `WHERE` currently supports only equality on the primary-key column
- rows still need to fit inside a single table leaf page because page splits are not implemented yet

**How you can create your own DB and query it today:**

```csharp
using Sqlity.Query;

using var engine = new QueryEngine("my-db.sqlity");
engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);");
engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");

var result = engine.Execute("SELECT id, name FROM users WHERE id = 1;");
```

That file path is the database. If `my-db.sqlity` does not exist, Sqlity creates it; if it exists, Sqlity reopens it.

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
