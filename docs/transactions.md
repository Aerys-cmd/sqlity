# Transactions and durability

Sqlity implements transactions using a **rollback journal** — the same mechanism SQLite uses in
its default journal mode.

## How it works

When a transaction begins the engine creates a sidecar `<db>.journal` file.  Before overwriting
any page, the engine first appends the *original* bytes of that page to the journal.

```
Transaction lifecycle
─────────────────────
BEGIN
  → create <db>.journal
  → write journal header: magic | original page count | original header page

WritePage(p)          (first time for page p in this transaction)
  → append p's current bytes to journal
  → then overwrite p in the data file

COMMIT
  → fsync data file
  → delete journal

ROLLBACK  (or crash recovery)
  → restore each journaled page from journal to data file
  → restore header page and original page count (truncate new pages)
  → delete journal
```

The invariant is: **as long as the journal exists, the data file may be partially updated**.
Deleting the journal is the atomic commit point.

## Journal file format

```
Offset    Size    Field
───────────────────────────────────────────
0         4       Magic "SJRL"
4         4       Original page count (uint32, little-endian)
8         4096    Original header page (page 0) snapshot
────────────────────────────────────────────────── repeating page records ──
+0        4       Page number (uint32, little-endian)
+4        4096    Original page bytes
```

## Crash recovery

If the process crashes (power loss, kill signal) while a transaction is in progress, the journal
file remains on disk. The next time `StorageEngine.Open` (or `FilePager.RecoverIfNeeded`) is
called, Sqlity detects the stale journal and automatically rolls back the partial changes. The
database is left in the state it was in before the crashed transaction began.

There is **no data loss** for committed transactions: a transaction is only committed once the
journal has been deleted and the data file has been flushed to disk.

## Concurrency

Sqlity is a single-user embedded database. `FilePager` opens the database file with
`FileShare.None`, so only one `QueryEngine` (or `SqlityConnection`) can hold the file open at a
time. Concurrent access from multiple processes or threads is not supported.

## SQL syntax

```sql
BEGIN;
INSERT INTO orders VALUES (1, 'Widget', 49);
INSERT INTO orders VALUES (2, 'Gadget', 99);
COMMIT;

-- Undo a transaction
BEGIN;
DELETE FROM orders WHERE id = 2;
ROLLBACK;
```

Rules:
- `BEGIN` opens a new transaction. Nesting a second `BEGIN` throws an error.
- `COMMIT` flushes and closes the transaction. Subsequent `COMMIT` / `ROLLBACK` throw.
- `ROLLBACK` restores the database to the state before `BEGIN`. Subsequent calls throw.
- Any DML/DDL statement executed *outside* an explicit `BEGIN` auto-commits (single-statement
  transaction), consistent with SQLite's default behaviour.

## ADO.NET

Use the standard `DbConnection.BeginTransaction` / `DbTransaction.Commit` / `DbTransaction.Rollback` API:

```csharp
using var conn = new SqlityConnection("Data Source=shop.sqlity");
conn.Open();

using var tx = conn.BeginTransaction();
try
{
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "INSERT INTO orders VALUES (1, 'Widget', 49);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO orders VALUES (2, 'Gadget', 99);";
        cmd.ExecuteNonQuery();
    }
    tx.Commit();
}
catch
{
    tx.Rollback();
    throw;
}
```

Disposing a `SqlityTransaction` without calling `Commit` automatically calls `Rollback`:

```csharp
using (var tx = conn.BeginTransaction())
{
    // ... statements ...
    // tx.Dispose() here will rollback if Commit was not called
}
```

Disposing a `SqlityConnection` with an active transaction automatically rolls back before
releasing the file handle.

## Isolation level

Sqlity is single-writer. `SqlityTransaction.IsolationLevel` reports `Serializable` because
that is the effective guarantee — only one transaction can be active at a time and it sees a
fully consistent snapshot.
