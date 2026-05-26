# Query engine notes

Sqlity now includes a small executable query layer on top of the storage engine. It is intentionally constrained so the code stays close to the underlying page and catalog behavior.

## Current MVP responsibilities

- tokenize a very small SQL subset
- parse `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, and `UPDATE`
- bind table and column names against catalog metadata
- convert SQL literals into storage-backed row values
- execute primary-key lookup, full table iteration, row deletion, and row update
- evaluate `INNER JOIN` and `LEFT JOIN` with flat column contexts
- evaluate compound `WHERE` expressions with `AND`/`OR` and all comparison operators

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
- blob literals use hex syntax like `X'CAFE'`

### `SELECT`

Supported shapes:

```sql
SELECT * FROM users;
SELECT id, name FROM users WHERE id = 2;
SELECT users.name, orders.amount FROM users
    INNER JOIN orders ON users.id = orders.user_id
    WHERE orders.amount > 50;
SELECT users.name, orders.amount FROM users
    LEFT JOIN orders ON users.id = orders.user_id;
```

Rules:

- projections can be `*` or an explicit column list
- column references may optionally be table-qualified (`users.name`)
- `WHERE` supports any column, any comparison operator, and compound `AND`/`OR` expressions (see [WHERE expressions](#where-expressions))
- when no `WHERE` is given, a full table scan is performed
- primary-key equality filters use a B+ tree point lookup; all other filters fall back to a full scan

### `INNER JOIN` / `LEFT JOIN`

Supported shapes:

```sql
SELECT users.name, orders.amount
    FROM users
    INNER JOIN orders ON users.id = orders.user_id;

SELECT users.name, orders.amount
    FROM users
    LEFT JOIN orders ON users.id = orders.user_id;
```

Rules:

- `JOIN` without a qualifier is treated as `INNER JOIN`
- the `ON` clause must be a column-equality condition in the form `table1.col = table2.col`
- multiple `JOIN` clauses can be chained in a single query
- self-joins are not supported
- when a column name exists in more than one joined table, it must be qualified with a table name; an ambiguous unqualified reference throws an error

### `DELETE`

Supported shapes:

```sql
DELETE FROM users WHERE id = 2;
DELETE FROM users WHERE active = FALSE;
DELETE FROM users WHERE score < 50 AND active = FALSE;
```

Rules:

- `WHERE` is required and supports any column and any comparison operator
- all rows that satisfy the condition are deleted; multiple rows may be affected
- primary-key equality is optimised to a B+ tree point delete; non-PK filters scan all rows

### `UPDATE`

Supported shapes:

```sql
UPDATE users SET name = 'Ada Lovelace', is_active = TRUE WHERE id = 1;
UPDATE users SET active = TRUE WHERE active = FALSE;
```

Rules:

- `WHERE` is required and supports any column and any comparison operator
- all rows that satisfy the condition are updated; multiple rows may be affected
- updating the primary key column is not allowed
- all non-assigned columns retain their existing values

### WHERE expressions

`WHERE` clauses accept a fully composable expression tree:

- **comparison operators**: `=`, `<>`, `<`, `>`, `<=`, `>=`
- **logical operators**: `AND`, `OR`
- **grouping**: parentheses to control evaluation order
- **column references**: bare names or table-qualified names (`table.column`)
- blob columns support only `=` and `<>`

Examples:

```sql
WHERE id = 1
WHERE name = 'Ada' AND active = TRUE
WHERE val = 1 OR val = 9
WHERE a = 1 AND (b = 0 OR b = 1)
WHERE score >= 75
WHERE users.id = 1
```

## Deliberate limitations

- no grouping, ordering, or aggregates
- no `ALTER TABLE`
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
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;"
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "DELETE FROM users WHERE id = 1;"
```

For more executable CLI examples with captured output, see `samples/Sqlity.Cli/README.md`.

The current runtime limits are:

- `WHERE` on non-primary-key columns performs a full table scan
- no subqueries, aggregates, or window functions
- no `NULL` support
