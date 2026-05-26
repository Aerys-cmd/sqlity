# Next roadmap

The storage and core query layer is in place: B+ tree with multi-page support, full CRUD, compound `WHERE`, and `JOIN`. The next steps close correctness gaps, widen the SQL surface, and wire up the public provider APIs.

## 1. ADO.NET provider ✅

- expose `DbConnection`, `DbCommand`, and `DbDataReader` — **done**
- adapt the existing `QueryEngine` result model instead of duplicating execution logic — **done**
- surface table metadata and column ordinals through provider-friendly APIs — **done**

## 2. Correctness gaps ✅

- `NULL` support — nullable columns, `NULL` literals, `NOT NULL` constraints, `IS NULL` / `IS NOT NULL` expressions — **done**
- page recycling — emptied B+ tree leaf pages are now released back to the free-list via `ReleasePage` — **done**

## 3. Transactions and durability

- add `BEGIN` / `COMMIT` / `ROLLBACK` transaction boundaries
- evaluate rollback logging or WAL
- document crash-recovery invariants once writes span multiple pages

## 4. Secondary indexes and query planning

- parse and store `CREATE INDEX` in the catalog
- build a secondary B+ tree per index
- introduce a logical/physical execution split so the planner can choose between a full scan and an index seek

## 5. Wider SQL surface

- `ORDER BY` (with `ASC` / `DESC`)
- `LIMIT` / `OFFSET`
- aggregate functions: `COUNT`, `SUM`, `MIN`, `MAX`, `AVG`
- `GROUP BY` / `HAVING`
- scalar subqueries and `IN (subquery)`
- `DROP TABLE` and `ALTER TABLE`
- additional types: `REAL` / `FLOAT`, `DATE`, `DATETIME`
- multi-statement batch execution in a single `Execute` call

## 6. EF Core provider

- implement the EF Core provider once the ADO.NET surface is stable

## 7. Developer workflow

- add a tiny CLI that accepts SQL text and prints result rows
- document the minimal `QueryEngine(filePath)` path for creating or reopening a `.sqlity` file
