# Next roadmap

The foundation is solid: B+ tree storage, full CRUD, compound `WHERE`, `JOIN`, secondary indexes
with a rule-based query planner, rollback-journal transactions, crash recovery, a complete ADO.NET
provider, and an EF Core 10 provider. All previous milestones are done.

The next steps widen the SQL surface, harden the storage engine, and improve developer experience.
Items are ordered by impact-to-effort ratio within each phase.

---

## Completed milestones

- ADO.NET provider (`SqlityConnection`, `SqlityCommand`, `SqlityDataReader`, `SqlityParameter`) ✅
- `NULL` support and page recycling ✅
- Transactions: `BEGIN` / `COMMIT` / `ROLLBACK`, rollback journal, crash recovery, auto-commit ✅
- Secondary indexes and rule-based query planner (index seek, ordered index scan) ✅
- `ORDER BY` (multi-column, index-aware), `LIMIT` / `OFFSET` ✅
- Aggregate functions: `COUNT`, `SUM`, `MIN`, `MAX`, `AVG`; `GROUP BY` / `HAVING` ✅
- Scalar subqueries and `IN (subquery)` ✅
- `DROP TABLE` and full `ALTER TABLE` suite ✅
- Additional column types: `REAL` / `FLOAT`, `DATE`, `DATETIME` ✅
- EF Core 10 provider with `UseSqlity`, LINQ translation, `EnsureCreated` / `EnsureDeleted` ✅
- CLI with single-statement and stdin-piping modes ✅
- `LIKE` / `ILIKE`, `BETWEEN` / `NOT BETWEEN`, `NOT IN`, `SELECT DISTINCT`, UPDATE/DELETE without `WHERE`, multi-row `INSERT`, column aliases, `COALESCE` / `NULLIF` / `IFNULL` ✅
- `DEFAULT expr`, `AUTOINCREMENT` / `SERIAL`, inline `UNIQUE`, `INSERT OR REPLACE`, `INSERT INTO t SELECT`, `CREATE VIEW`, `TRUNCATE TABLE` ✅
- Scalar functions: `UPPER`, `LOWER`, `TRIM`, `LENGTH`, `SUBSTR`, `REPLACE`, `ABS`, `ROUND`, `CEIL`, `FLOOR` ✅

---

## Phase 1 — SQL completeness (missing basics)

✅ **Complete.** All items implemented and shipped.

- `LIKE` operator with `%` and `_` wildcards; optional case-insensitive `ILIKE`
- `BETWEEN x AND y` / `NOT BETWEEN` as syntactic sugar over two comparisons
- `DISTINCT` keyword in `SELECT` — deduplicate result rows after projection
- `NOT IN (values | subquery)` — complement of the existing `IN` implementation
- UPDATE and DELETE without a `WHERE` clause — affect all rows (currently the parser requires `WHERE`)
- Multi-row `INSERT`: `VALUES (…), (…), …`
- Column aliases: `SELECT col AS alias` — carry alias through to result column names
- `COALESCE(a, b, …)`, `NULLIF(a, b)`, `IFNULL(a, b)` scalar functions

---

## Phase 2 — DDL completeness

✅ **Complete.** All items implemented and shipped.

- `DEFAULT expr` in `CREATE TABLE` — parsed, stored in catalog (Version 3 schema format), applied on INSERT when column omitted ✅
- `AUTOINCREMENT` / `SERIAL` for `INT64` primary keys — auto-assign max+1 on INSERT when column omitted ✅
- Inline `UNIQUE` constraint in `CREATE TABLE` — creates implicit unique index (`uq_{table}_{col}`) automatically ✅
- `INSERT OR REPLACE` (upsert) — detects primary-key conflict and overwrites instead of erroring ✅
- `INSERT INTO t SELECT …` — pipes a query result directly into an insert loop ✅
- `CREATE VIEW name AS SELECT …` — stores view definition in catalog, materialises on query ✅
- `TRUNCATE TABLE` — deletes all rows and releases all pages back to the free list ✅

---

## Phase 3 — Advanced SQL

- ✅ Scalar functions: `UPPER`, `LOWER`, `TRIM`, `LENGTH`, `SUBSTR`, `REPLACE`, `ABS`, `ROUND`, `CEIL`, `FLOOR`
- ✅ `CASE WHEN cond THEN expr … ELSE expr END` expressions in SELECT and WHERE
- ✅ `EXISTS (SELECT …)` / `NOT EXISTS (SELECT …)` as WHERE atoms
- `UNION` / `UNION ALL` — combine two SELECT results
- `INTERSECT` / `EXCEPT` — set-difference operations
- Common Table Expressions: `WITH name AS (SELECT …) [, …] SELECT …` (CTEs materialised as temp tables)
- `SAVEPOINT name` / `RELEASE name` / `ROLLBACK TO name` — nested transaction savepoints (DO WE REALLY NEED THIS ?)
- Window functions: `ROW_NUMBER()`, `RANK()`, `DENSE_RANK()`, `LAG()`, `LEAD()` with `OVER (PARTITION BY … ORDER BY …)`

---

## Phase 4 — Storage engine hardening

Each item here teaches a concrete internals concept:

- **Buffer pool with LRU eviction** — wrap `IPager` with a fixed-capacity in-memory page cache;
  dirty pages flushed on eviction or commit; demonstrates the buffer-pool design used by every
  real database
- **Overflow pages for large rows** — detect rows exceeding page capacity, chain overflow pages,
  update B-tree insert / read / delete paths; lifts the current hard per-row size limit
- **Write-Ahead Logging (WAL)** — implement WAL as an alternative to the rollback journal;
  readers see a consistent snapshot while a writer appends; demonstrates why SQLite and PostgreSQL
  converge on WAL for concurrent workloads
- **In-memory mode** — detect `":memory:"` as the file path and substitute `InMemoryPager`
  (already used in benchmarks) so no file I/O occurs; useful for tests and embedded scenarios
- **Cost-based query planner** — collect per-table row counts and per-column distinct-value
  estimates; replace the current rule-based index selection with cardinality-driven cost estimates;
  demonstrates how statistics drive real query optimisers

---

## Phase 5 — Provider and developer experience

- **Async ADO.NET** — implement `OpenAsync`, `ExecuteReaderAsync`, `ExecuteNonQueryAsync`,
  `ExecuteScalarAsync` on all provider types; required for async EF Core paths
- **`GetSchemaTable`** in `SqlityDataReader` — return column metadata (name, type, ordinal,
  nullable) so schema-aware consumers work out of the box
- **`EXPLAIN QUERY PLAN`** statement — parse `EXPLAIN QUERY PLAN SELECT …` and return plan
  description rows (scan vs seek, index used, estimated rows) instead of data rows
- **Error messages with source position** — track line and column in `SqlTokenizer`; include
  position in all parse and bind errors so diagnostics are actionable
- **Interactive CLI REPL** — when no SQL argument is provided, start a read-eval-print loop
  accepting multi-line input terminated by `;`, with `\q` to exit; makes the CLI useful for
  exploratory queries
- **NuGet packaging** — add `<PackageId>`, `<Version>`, and `<Description>` to `Sqlity.Ado`
  and `Sqlity.EFCore`; set up a GitHub Actions publish workflow
- **EF Core migrations** — implement `IMigrationsSqlGenerator` and related services so
  `dotnet-ef migrations add` and `dotnet-ef database update` work against Sqlity
