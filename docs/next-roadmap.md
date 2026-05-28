# Next roadmap

The storage and core query layer is in place: B+ tree with multi-page support, full CRUD, compound `WHERE`, and `JOIN`. The next steps close correctness gaps, widen the SQL surface, and wire up the public provider APIs.

## 1. ADO.NET provider ✅

- expose `DbConnection`, `DbCommand`, and `DbDataReader` — **done**
- adapt the existing `QueryEngine` result model instead of duplicating execution logic — **done**
- surface table metadata and column ordinals through provider-friendly APIs — **done**

## 2. Correctness gaps ✅

- `NULL` support — nullable columns, `NULL` literals, `NOT NULL` constraints, `IS NULL` / `IS NOT NULL` expressions — **done**
- page recycling — emptied B+ tree leaf pages are now released back to the free-list via `ReleasePage` — **done**

## 3. Transactions and durability ✅

- `BEGIN` / `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` transaction boundaries — **done**
- rollback journal (page-level journaling before each write; journal deleted on commit) — **done**
- crash recovery: stale journal on reopen triggers automatic rollback — **done**
- `SqlityTransaction.Commit()` and `Rollback()` wired to real storage operations — **done**
- auto-commit for statements executed outside an explicit `BEGIN` — **done**
- multi-statement batch execution in a single `Execute` / `ExecuteNonQuery` call — **done**

## 4. Secondary indexes and query planning ✅

- parse and store `CREATE [UNIQUE] INDEX` in a dedicated index catalog — **done**
- build a secondary B+ tree per index with sort-preserving key encoding — **done**
- automatic index maintenance on `INSERT`, `DELETE`, and `UPDATE` — **done**
- rule-based logical/physical query planner: equality predicates on leading index columns produce an index seek; unmatched predicates become a post-filter — **done**

## 5. Wider SQL surface ✅ (core subset)

- `ORDER BY` (with `ASC` / `DESC`), multi-column, index-aware optimization — **done**
- `LIMIT` / `OFFSET` — **done**
- aggregate functions: `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` — **done**
- `GROUP BY` / `HAVING` — **done**
- scalar subqueries and `IN (subquery)`
- `DROP TABLE` and `ALTER TABLE` — **done** (`DROP TABLE`, `ALTER TABLE … RENAME TO`, `ALTER TABLE … ADD COLUMN [NOT NULL]`, `ALTER TABLE … RENAME COLUMN … TO`)
- additional types: `REAL` / `FLOAT`, `DATE`, `DATETIME` — **done**

## 6. EF Core provider

- implement the EF Core provider once the ADO.NET surface is stable

## 7. Developer workflow ✅

- add a tiny CLI that accepts SQL text and prints result rows — **done**
- document the minimal `QueryEngine(filePath)` path for creating or reopening a `.sqlity` file — **done**
