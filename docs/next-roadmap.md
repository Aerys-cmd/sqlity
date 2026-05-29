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
- `CASE WHEN … THEN … END` expressions, `EXISTS` / `NOT EXISTS`, `UNION` / `UNION ALL` / `INTERSECT` / `EXCEPT`, CTEs (`WITH … AS`), window functions (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LAG`, `LEAD`) ✅
- In-memory mode, buffer pool (LRU), overflow pages ✅

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

✅ **Complete.** All items implemented and shipped.

- `UPPER`, `LOWER`, `TRIM`, `LENGTH`, `SUBSTR`, `REPLACE`, `ABS`, `ROUND`, `CEIL`, `FLOOR` scalar functions ✅
- `CASE WHEN cond THEN expr … ELSE expr END` expressions in SELECT and WHERE ✅
- `EXISTS (SELECT …)` / `NOT EXISTS (SELECT …)` as WHERE atoms ✅
- `UNION` / `UNION ALL`, `INTERSECT`, `EXCEPT` — set operations ✅
- Common Table Expressions: `WITH name AS (SELECT …) [, …] SELECT …` (CTEs materialised as temp tables) ✅
- Window functions: `ROW_NUMBER()`, `RANK()`, `DENSE_RANK()`, `LAG()`, `LEAD()` with `OVER (PARTITION BY … ORDER BY …)` ✅

---

## Phase 4 — Storage engine hardening

✅ **Complete.** All items implemented and shipped.

- **In-memory mode** — `StorageEngine.Open(":memory:")` uses `InMemoryPager`; no file I/O; useful for tests and embedded scenarios ✅
- **Buffer pool with LRU eviction** — `BufferedPager` wraps any `IPager` with a capacity-bounded LRU cache; dirty pages flushed on eviction or commit ✅
- **Overflow pages for large rows** — rows exceeding page capacity spill across chained overflow pages; B-tree insert / read / delete paths updated; hard per-row size limit lifted ✅
- **Write-Ahead Logging (WAL)** — removed; `WalPager` was an immediate-checkpoint WAL that provided no advantage over the default `BufferedPager(FilePager)` stack
- **Cost-based query planner** — `ANALYZE [table_name]` collects per-table row counts and per-column distinct-value estimates; `QueryPlanner` uses cardinality-driven cost model (`rowCount × Π(1/ndv_col)`) to pick the most selective index seek, falling back to rule-based scoring when no statistics are available ✅
- **Persistent query-planner statistics** — stats are stored in an on-disk `__sqlity_stat1` catalog page (format version 4) and loaded automatically on engine open, SQLite-style; DDL operations (`DROP TABLE`, `RENAME TABLE`, `ADD COLUMN`, `RENAME COLUMN`, `TRUNCATE TABLE`) invalidate or migrate stats; lazy auto-analyze from the planner is memory-only (no hidden writes); explicit `ANALYZE` persists to disk with best-effort semantics (full catalog never crashes a query) ✅

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
