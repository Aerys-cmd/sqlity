# Storage engine architecture

This document explains the first storage-engine design for Sqlity. The target is an educational engine that is simple enough to study end-to-end, but still follows real database ideas closely.

## 1. Page-based storage

### Concept

A database file is divided into fixed-size blocks called pages. Sqlity uses 4096-byte pages from the beginning.

### Why databases use it

- The operating system already reads and writes files in blocks.
- A fixed page size makes page caching, addressing, and recovery rules simpler.
- B-trees naturally map nodes to pages.
- Fragmentation and free-space accounting become easier when the unit of storage is fixed.

### Tradeoffs

- Fixed-size pages waste some space when rows are tiny.
- Large rows may need overflow handling later.
- Page splitting and compaction logic become necessary once writes are added.

### Sqlity choice

- Page `0` is reserved for the database header page.
- Pages `1..N` are regular storage pages.
- All page numbers are stable integer addresses inside the file.

## 2. Database header

### Concept

The database header is file-wide metadata that tells the engine how to interpret the rest of the file.

### Why databases use it

- The engine needs a magic value to recognize valid files.
- Page size and format version must be stored on disk.
- Root pages, schema versions, and free-list state need a single canonical location.

### Tradeoffs

- A richer header makes bootstrapping easier but couples more metadata to a single location.
- A minimal header is simpler but pushes more discovery work elsewhere.

### Initial Sqlity header format

All values are little-endian.

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 8 | Magic `"SQLITYDB"` |
| 8 | 4 | Format version |
| 12 | 2 | Page size |
| 14 | 2 | Header size |
| 16 | 4 | Page count |
| 20 | 4 | Root page id |
| 24 | 4 | Free-list head page id |
| 28 | 4 | Free page count |
| 32 | 4 | Schema version |
| 36 | 28 | Reserved for future metadata |

The header fits in the first 64 bytes of page `0`. The rest of page `0` is currently unused and reserved for future metadata.

## 3. Page layout

### Concept

Regular pages use a slotted-page layout:

- page header at the front
- slot/pointer array growing forward
- cell payloads growing backward from the end of the page

This is the same family of design used by many real databases because it supports variable-length rows while keeping records movable inside the page.

### Why databases use it

- Variable-length rows can be compacted without changing slot indexes.
- B-tree cells can be rearranged during inserts and splits.
- Free space is easy to compute as the gap between slot metadata and payload data.

### Tradeoffs

- Slotted pages are slightly harder to understand than fixed-record pages.
- Deletes create fragmentation unless the engine compacts or tracks free blocks.
- The page header needs careful invariants.

### Initial regular page header format

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 1 | Page type |
| 1 | 1 | Flags |
| 2 | 2 | Cell count |
| 4 | 2 | First free block offset |
| 6 | 2 | Cell content start |
| 8 | 4 | Page number |
| 12 | 4 | Special page id |
| 16 | 2 | Fragmented free bytes |
| 18 | 2 | Reserved |

Page type distinguishes table leaf, table internal, free-list, and overflow pages. `SpecialPageId` is intentionally generic so later iterations can use it as a right-sibling pointer or another page-specific link without redesigning the base header.

## 4. Row serialization

### Concept

Rows must be converted into bytes before they can be placed in a page. The row format determines correctness, simplicity, and future compatibility.

### Why databases use explicit row formats

- Disk storage needs stable binary layouts.
- The engine must be able to decode values without depending on CLR object layouts.
- Explicit serialization makes corruption easier to detect and tests easier to write.

### Tradeoffs

- Self-describing rows are easier to inspect but store more metadata.
- Schema-only rows are smaller but depend on external metadata being perfect.
- Supporting `NULL` and many types early makes the first iteration much more complex.

### Initial Sqlity row format

For the MVP, rows are schema-bound and store only a few types:

- `Int64`
- `String`
- `Blob`
- `Boolean`

The row format is:

| Field | Size |
| --- | ---: |
| Row format version | 1 byte |
| Column count | 1 byte |
| Repeated per column: type tag | 1 byte |
| Repeated per column: payload length | 2 bytes |
| Repeated per column: payload | variable |

This intentionally stores the type tag even though the schema already knows it. The redundancy makes early debugging and corruption detection easier, which is worth the overhead in an educational engine.

For now:

- `NULL` is not supported yet.
- the primary key must be `Int64`
- strings are UTF-8 encoded
- blobs are raw bytes

## 5. Free-page management

### Concept

When rows or pages are deleted, the engine should recycle pages instead of always extending the file.

### Why databases use it

- Reusing pages limits file growth.
- It avoids unnecessary I/O and space waste.
- It provides a base for later vacuuming or compaction strategies.

### Tradeoffs

- Tracking free pages inside the file complicates writes slightly.
- A simple free list is easy to reason about but not as space-efficient as bitmap schemes at scale.

### Initial Sqlity approach

Use a singly linked free list:

- the database header stores the head page id
- each free page stores the next free page id in its payload
- allocation first consumes the free list, then extends the file if needed

This is easy to inspect in code and on disk. A free-page bitmap can come later if it becomes educationally valuable.

## 6. Why B-trees?

### Concept

A B-tree stores sorted keys across many pages while keeping the tree shallow.

### Why databases use it

- Point lookups are efficient because the tree height stays small.
- Inserts and scans both work well with ordered pages.
- The structure maps directly onto fixed-size pages.

### Tradeoffs

- B-tree code is harder than append-only heaps.
- Splits, merges, and rebalancing need careful invariants.
- The page format must reserve enough metadata for navigation.

### Sqlity choice

The initial storage design is already B-tree-oriented even before full B-tree mutation logic exists:

- regular pages have slotted-page metadata
- table leaf cells store `primary-key + payload`
- future internal pages will store separator keys and child page ids

This lets the project grow incrementally without throwing away the first page format.

## 7. Proposed namespaces and classes

### `Sqlity.Core`

- `DbConstants`: shared file-format constants

### `Sqlity.Storage.Abstractions`

- `IPager`: minimal page device contract

### `Sqlity.Storage.Headers`

- `DatabaseHeader`: file-wide metadata serializer

### `Sqlity.Storage.Pages`

- `PageType`: page-kind enumeration
- `PageHeader`: regular page metadata
- `PageBuffer`: fixed-size in-memory page buffer
- `FreeListPage`: free-page link helpers

### `Sqlity.Storage.Rows`

- `ColumnType`: MVP storage types
- `ColumnDefinition`: schema column metadata
- `TableSchema`: MVP table schema definition
- `RowSerializer`: schema-bound row codec

### `Sqlity.Storage.Catalog`

- `TableInfo`: persisted table metadata surface
- `TableSchemaSerializer`: schema blob codec used by the system catalog
- `CatalogStore`: system catalog reader/writer over a table leaf page

### `Sqlity.Storage.BTree`

- `BTreePageLayout`: slot and cell layout constants
- `TableLeafCell`: initial leaf cell binary format

### `Sqlity.Storage.IO`

- `FilePager`: single-file fixed-page reader/writer/allocator

### `Sqlity.Storage`

- `StorageEngine`: table creation, row insertion, catalog bootstrap, and row reads over the pager

## 8. Initial page format proposal

### Page 0: database header page

- bytes `0..63`: `DatabaseHeader`
- bytes `64..4095`: reserved for future metadata

`DatabaseHeader.RootPageId` now points to the system catalog root page once the storage engine bootstraps the file.

### System catalog page

- stored as a normal table leaf page
- one row per user table
- persists:
  - table id
  - table name
  - user table root page id
  - serialized `TableSchema`

Using a normal table leaf page here keeps metadata on the same storage path as user data and avoids inventing a second persistence format for the MVP.

### Table leaf pages

- generic `PageHeader`
- slot array of `ushort` cell offsets
- each cell payload encoded as:
  - `Int64` primary key
  - `UInt16` payload length
  - row payload bytes

The current implementation supports:

- ordered insertion by primary key
- duplicate-key rejection within a page
- binary-search lookup inside the page
- row deletion with correct slotted-page compaction (pointer values, pointer array slot removal, and cell content block shift)
- row update: in-place overwrite for same-size payload; safe delete-then-reinsert with space preflight for size-changed payload
- persistence through `FilePager`
- catalog-backed table discovery after reopening the file
- table-level row operations through `StorageEngine`

Current limitations:

- no page split yet
- no overflow handling for large rows yet

### Free-list pages

- generic `PageHeader` with type `FreeList`
- first payload field stores the next free page id
- remaining bytes ignored for now

## 9. Incremental roadmap

1. **Foundation**: header, pages, row codec, pager, free-list, docs, and tests.
2. **Writable table leaf pages**: cell insertion, slot management, free-space accounting. Completed for a single page.
3. **Table catalog**: schema persistence and reopen support. Implemented on top of a system catalog leaf page.
4. **MVP query execution**: minimal parser + binder + executor for `CREATE TABLE`, `INSERT`, and `SELECT`. Implemented for single-page tables.
5. **Delete and update**: slotted-page compaction, row deletion, and in-place/resize-safe row update. Implemented for single-page tables. `DELETE` and `UPDATE` SQL statements fully supported.
6. **B-tree navigation**: root-page search across multiple pages and leaf splits.
7. **Provider integration**: ADO.NET surface once multi-page query execution is stable.
8. **Advanced durability**: transactions, rollback/WAL, and crash-recovery experiments.

## 11. Writable table leaf page behavior

The new `TableLeafPage` type is intentionally concrete rather than generic.

### Concept

It wraps a `PageBuffer` whose `PageHeader.PageType` is `TableLeaf` and provides the first real write path for table rows stored as B-tree leaf cells.

### Why databases use a slot array here

- sorted logical order can differ from physical byte order
- inserts only need to shift 2-byte cell pointers, not full row payloads
- binary search can operate over keys in slot order

### Tradeoffs

- inserts still become expensive once a page is dense because the pointer array shifts
- the implementation is page-local only; tree-wide balancing comes later

### Current design

- `TryInsert` performs a binary search over primary keys; duplicate keys return `DuplicateKey`; insufficient space returns `PageFull`; successful inserts write the cell at the back of the page and insert a new slot pointer in sorted order
- `TryDelete` removes a cell by its primary key: locates the slot via binary search, compacts the cell content block toward higher offsets, updates all pointer values that shifted, removes the pointer array slot, and updates the header (`CellCount`, `CellContentStart`)
- `TryUpdate` handles same-size payloads with an in-place overwrite; for size-changed payloads it pre-checks available free space before deleting the old cell, then reinserts at the correct sorted position
- `TryGetCell` and `ReadAllCells` decode the sorted page contents

## 10. Why this is a good MVP

- It is small enough to understand in one codebase.
- It exposes real database tradeoffs instead of hiding them.
- It does not lock the project into a throwaway design.
- It leaves clear seams for multi-page B-tree work and provider layers later.
