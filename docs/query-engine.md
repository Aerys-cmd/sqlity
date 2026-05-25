# Query engine notes

Sqlity now includes a small executable query layer on top of the storage engine. It is intentionally constrained so the code stays close to the underlying page and catalog behavior.

## Current MVP responsibilities

- tokenize a very small SQL subset
- parse `CREATE TABLE`, `INSERT`, and `SELECT`
- bind table and column names against catalog metadata
- convert SQL literals into storage-backed row values
- execute primary-key lookup and full table iteration

## Supported SQL surface

### `CREATE TABLE`

Supported shape:

```sql
CREATE TABLE users (
    id INT64 PRIMARY KEY,
    name STRING,
    is_active BOOLEAN
);
```

Rules:

- exactly one inline `PRIMARY KEY` column is required
- the primary key must resolve to `INT64`
- type aliases are intentionally narrow: `INT64`/`INTEGER`/`BIGINT`, `STRING`/`TEXT`, `BLOB`, `BOOLEAN`/`BOOL`

### `INSERT`

Supported shapes:

```sql
INSERT INTO users VALUES (1, 'Ada', TRUE);
INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);
```

Rules:

- every column must receive a value because `NULL` is not supported yet
- duplicate primary keys are rejected
- rows must still fit inside a single table leaf page because page splits are not implemented yet
- blob literals use hex syntax like `X'CAFE'`

### `SELECT`

Supported shapes:

```sql
SELECT * FROM users;
SELECT id, name FROM users WHERE id = 2;
```

Rules:

- projections can be `*` or an explicit column list
- `WHERE` currently supports only equality on the primary key column
- scans and lookups operate directly over the single root leaf page for the table

## Deliberate limitations

- no joins, grouping, ordering, or aggregates
- no `UPDATE`, `DELETE`, or `ALTER TABLE`
- no table constraints beyond the inline primary key
- no query planner: execution maps almost directly to storage operations

These constraints are intentional. The goal of this milestone is to connect SQL text to the persisted catalog and row/page storage without hiding the mechanics behind a large abstraction layer.

## First user-owned database workflow

The smallest library entry point is `QueryEngine(filePath)`. Passing a `.sqlity` path opens that database file if it already exists or creates a new one if it does not.

The repository also includes a tiny runnable CLI in `samples/Sqlity.Cli`:

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);"
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "INSERT INTO users VALUES (1, 'Ada', TRUE);"
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "SELECT id, name FROM users WHERE id = 1;"
```

For more executable CLI examples with captured output, see `samples/Sqlity.Cli/README.md`.

The workflow is intentionally focused on `CREATE TABLE`, `INSERT`, and `SELECT`. The current runtime limits are still the same:

- rows must fit inside a single table leaf page because page splits are not implemented yet
- `WHERE` supports only equality on the primary-key column
