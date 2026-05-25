# Next roadmap

The current milestone connected persisted storage to executable SQL. The next steps should deepen correctness and widen the public surface without skipping the core database mechanics.

## 1. Multi-page table navigation

- add root-to-leaf search instead of assuming one leaf page per table
- implement leaf-page split behavior
- update `StorageEngine` and query execution so inserts can grow past a single page

## 2. Delete and page maintenance

- add row delete support
- compact fragmented table leaf pages
- recycle emptied pages through the existing free-list path

## 3. ADO.NET provider MVP

- expose `DbConnection`, `DbCommand`, and `DbDataReader`
- adapt the existing `QueryEngine` result model instead of duplicating execution logic
- surface table metadata and column ordinals through provider-friendly APIs

## 4. Better query semantics

- extend `WHERE` beyond primary-key equality
- add `UPDATE` and `DELETE`
- introduce a simple logical/physical execution split once more than one access path exists

## 5. Durability experiments

- add transaction boundaries
- evaluate rollback logging or WAL
- document crash-recovery invariants once writes span multiple pages

## 6. First user-owned database workflow

- document the minimal `QueryEngine(filePath)` path so a caller can create or reopen a `.sqlity` file
- add a tiny sample app or CLI that accepts SQL text and prints result rows
- keep the first runnable workflow focused on `CREATE TABLE`, `INSERT`, and `SELECT`
- make current limits explicit: single-page tables and `WHERE` only on the primary key
