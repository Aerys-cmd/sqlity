# Sqlity architecture

Sqlity is split into four layers so the repository mirrors how embedded databases usually evolve:

## 1. Storage Engine

Owns the database file, fixed-size pages, binary serialization, free-page tracking, and B-tree-oriented data structures. This is the first subsystem because every higher layer depends on stable storage semantics.

## 2. Query Engine

Owns SQL tokenization, parsing, binding, logical operations, and execution against storage primitives. The current query engine is deliberately small and supports `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, `UPDATE`, `CREATE INDEX`, `CREATE UNIQUE INDEX`, and compound `WHERE` expressions. A rule-based logical/physical query planner chooses between full scans and secondary-index seeks based on available indexes and predicate coverage.

## 3. ADO.NET Provider

Owns `DbConnection`, `DbCommand`, `DbDataReader`, parameter handling, and provider-specific behaviors. It should adapt the query engine instead of duplicating database logic.

## 4. EF Core Provider

Owns EF Core metadata mapping, query translation, migrations integration strategy, and provider services. It should remain thin and rely on the ADO.NET provider plus query/storage capabilities that already exist.

## Why this split?

- It matches the dependency flow of a real embedded database.
- It keeps storage concepts visible instead of burying them under ORM concerns.
- It allows incremental milestones with meaningful GitHub history.
- It keeps later provider work honest: the higher layers only exist once the lower layers can support them.

## Current implementation status

- `Sqlity.Core`: started
- `Sqlity.Storage`: multi-page B+ tree storage engine, persisted catalog, full row DML (insert/delete/update) with slotted-page compaction, leaf-page splits, internal-page splits, stable root promotion, rollback-journal transactions, secondary B+ trees with sort-preserving key encoding, persisted index catalog, and automatic index maintenance on all DML operations implemented
- `Sqlity.Query`: SQL parser and executor for `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, `UPDATE`, `CREATE INDEX`, `CREATE UNIQUE INDEX`, `BEGIN`, `COMMIT`, and `ROLLBACK` implemented; rule-based logical/physical query planner with `QueryPlanner` and `QueryExecutor` selects index seeks over full scans when applicable
- `Sqlity.Ado`: fully implemented — `SqlityConnection`, `SqlityCommand`, `SqlityDataReader`, `SqlityParameter`, `SqlityParameterCollection`, `SqlityTransaction` (full commit/rollback support)
- `Sqlity.EFCore`: documented, code not implemented yet
