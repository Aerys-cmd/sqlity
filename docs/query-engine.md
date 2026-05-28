# Query engine notes

Sqlity now includes a small executable query layer on top of the storage engine. It is intentionally constrained so the code stays close to the underlying page and catalog behavior.

## Current MVP responsibilities

- tokenize a very small SQL subset
- parse `CREATE TABLE`, `INSERT`, `SELECT`, `DELETE`, `UPDATE`, `CREATE INDEX`, and `CREATE UNIQUE INDEX`
- bind table and column names against catalog metadata
- convert SQL literals (including `NULL`) into storage-backed row values
- execute primary-key lookup, full table iteration, row deletion, and row update
- evaluate `INNER JOIN` and `LEFT JOIN` with flat column contexts
- evaluate compound `WHERE` expressions with `AND`/`OR`, all comparison operators, `IS NULL` / `IS NOT NULL`, `LIKE` / `ILIKE`, `BETWEEN`, and `IN` / `NOT IN`
- choose between a secondary-index seek, an index-ordered scan, and a full scan based on available indexes and predicate/order-by coverage
- sort result rows by one or more columns (`ORDER BY`) with an optional index-ordered scan optimisation
- apply `LIMIT` and `OFFSET` to any result set
- evaluate aggregate functions (`COUNT`, `SUM`, `MIN`, `MAX`, `AVG`) with optional `GROUP BY` grouping and `HAVING` filtering
- deduplicate result rows with `SELECT DISTINCT`
- project result columns under optional aliases (`SELECT col AS alias`)
- evaluate scalar functions `COALESCE`, `NULLIF`, and `IFNULL` in `SELECT` lists

## Supported SQL surface

### Transaction control

```sql
-- BEGIN and BEGIN TRANSACTION are equivalent
BEGIN;
INSERT INTO orders VALUES (1, 'Widget', 49);
INSERT INTO orders VALUES (2, 'Gadget', 99);
COMMIT;

BEGIN TRANSACTION;
DELETE FROM orders WHERE id = 2;
ROLLBACK;

-- Multi-statement batch in one string
BEGIN; INSERT INTO orders VALUES (3, 'Sprocket', 15); COMMIT;
```

Rules:

- `BEGIN` and `BEGIN TRANSACTION` are equivalent. Nesting a second `BEGIN` throws.
- `COMMIT` persists all changes made since `BEGIN` and closes the transaction.
- `ROLLBACK` discards all changes made since `BEGIN` and closes the transaction.
- Any DML/DDL executed outside an explicit `BEGIN` auto-commits as a single-statement transaction.
- Multiple `;`-separated statements can be passed to a single `Execute` / `ExecuteNonQuery` call;
  they execute in order and the result of the last statement is returned.

See `docs/transactions.md` for crash-recovery invariants and the journal format.

### `CREATE INDEX` / `CREATE UNIQUE INDEX`

Supported shapes:

```sql
CREATE INDEX idx_users_email ON users (email);
CREATE UNIQUE INDEX idx_orders_ref ON orders (reference_number);
CREATE INDEX idx_orders_customer ON orders (customer_id, created_at);
```

Rules:

- index names must be unique across the database
- all indexed columns must exist in the target table
- `CREATE UNIQUE INDEX` rejects duplicate non-null values on insert or update; an existing duplicate does not prevent index creation (uniqueness is only enforced going forward)
- multi-column indexes are supported from the start; the planner scores leading equality columns and falls back to a full scan for any unmatched suffix
- indexes are stored in a dedicated index catalog (`__sqlity_indexes`) that persists across reopen
- index entries are automatically maintained on every `INSERT`, `DELETE`, and `UPDATE`

### `CREATE TABLE`

Supported shape:

```sql
CREATE TABLE users (
    id INT64 PRIMARY KEY,
    name STRING,
    score INT64 NOT NULL,
    is_active BOOLEAN
);
```

Rules:

- exactly one inline `PRIMARY KEY` column is required
- the primary key must resolve to `INT64`
- type aliases are intentionally narrow: `INT64`/`INTEGER`/`BIGINT`, `STRING`/`TEXT`, `BLOB`, `BOOLEAN`/`BOOL`
- columns are **nullable by default**; add `NOT NULL` to reject null values; `PRIMARY KEY` implies `NOT NULL`

### `INSERT`

Supported shapes:

```sql
INSERT INTO users VALUES (1, 'Ada', 90, TRUE);
INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);
INSERT INTO users (id) VALUES (3);          -- omitted nullable columns default to NULL
INSERT INTO users VALUES (4, NULL, 95, TRUE); -- explicit NULL literal

-- Multi-row insert
INSERT INTO users VALUES (5, 'Grace', 88, TRUE), (6, 'Alan', 72, FALSE);
INSERT INTO users (id, name) VALUES (7, 'Margaret'), (8, 'Edsger');
```

Rules:

- duplicate primary keys are rejected
- `NULL` is a valid literal for any nullable column
- omitting a nullable column in the named-column form inserts `NULL` for that column
- omitting a `NOT NULL` column in the named-column form throws an error
- blob literals use hex syntax like `X'CAFE'`
- multiple value rows can be supplied in a single statement; `RowsAffected` equals the number of rows inserted

### `SELECT`

Supported shapes:

```sql
SELECT * FROM users;
SELECT id, name FROM users WHERE id = 2;
SELECT id AS user_id, name AS full_name FROM users;    -- column aliases
SELECT DISTINCT cat FROM entries;                       -- deduplicate result rows
SELECT id, name FROM users ORDER BY name ASC;
SELECT id, name FROM users ORDER BY name ASC LIMIT 10 OFFSET 20;
SELECT dept, COUNT(*) AS cnt, SUM(salary) AS total FROM employees GROUP BY dept;
SELECT dept, COUNT(*) FROM employees GROUP BY dept HAVING COUNT(*) > 5;
SELECT users.name, orders.amount FROM users
    INNER JOIN orders ON users.id = orders.user_id
    WHERE orders.amount > 50;
SELECT users.name, orders.amount FROM users
    LEFT JOIN orders ON users.id = orders.user_id;

-- Scalar functions
SELECT COALESCE(nickname, name) AS display_name FROM users;
SELECT NULLIF(score, 0) AS adjusted FROM results;
SELECT IFNULL(bio, 'No bio') AS bio FROM profiles;
```

Rules:

- projections can be `*`, an explicit column list, aggregate expressions, or scalar function calls (`COALESCE`, `NULLIF`, `IFNULL`)
- any projected column or scalar function call can have an `AS alias` suffix; the alias becomes the output column name
- `SELECT DISTINCT` deduplicates rows after projection and before `LIMIT`/`OFFSET`
- column references may optionally be table-qualified (`users.name`)
- `WHERE` supports any column, any comparison operator, and compound `AND`/`OR` expressions (see [WHERE expressions](#where-expressions))
- when no `WHERE` is given, a full table scan is performed
- primary-key equality filters use a B+ tree point lookup; equality predicates on secondary-indexed columns trigger an index seek; all other predicates either become a post-filter or fall back to a full scan (see [Query planner](#query-planner))
- `ORDER BY` accepts one or more columns each with an optional `ASC` (default) or `DESC` direction; if a secondary index exists on the leading sort column the planner emits an `IndexOrderedScan` instead of an in-memory sort
- `LIMIT n` truncates the result to at most *n* rows; `OFFSET m` skips the first *m* rows; both can be combined and are applied after `DISTINCT` and any `ORDER BY`
- `GROUP BY` accepts one or more column names; every non-aggregate `SELECT` column must appear in `GROUP BY` (strict enforcement, throws otherwise)
- `HAVING` filters groups after aggregation; the expression must be a single aggregate comparison (e.g. `HAVING SUM(amount) >= 100`); the aggregate function used in `HAVING` need not appear in the `SELECT` list
- `AVG` always returns a `double`; `COUNT`, `SUM`, `MIN`, and `MAX` over integer columns return `INT64`; aggregate column names default to `Count(*)`, `Sum(amount)`, etc. but can be overridden with `AS`
- aggregate queries are not supported in `JOIN` paths

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
DELETE FROM users;                       -- no WHERE: deletes all rows
```

Rules:

- `WHERE` is optional; omitting it deletes **all rows** in the table
- all rows that satisfy the condition are deleted; multiple rows may be affected
- primary-key equality is optimised to a B+ tree point delete; non-PK filters scan all rows

### `UPDATE`

Supported shapes:

```sql
UPDATE users SET name = 'Ada Lovelace', is_active = TRUE WHERE id = 1;
UPDATE users SET active = TRUE WHERE active = FALSE;
UPDATE users SET score = 0;              -- no WHERE: updates all rows
```

Rules:

- `WHERE` is optional; omitting it updates **all rows** in the table
- all rows that satisfy the condition are updated; multiple rows may be affected
- updating the primary key column is not allowed
- all non-assigned columns retain their existing values

### WHERE expressions

`WHERE` clauses accept a fully composable expression tree:

- **comparison operators**: `=`, `<>`, `<`, `>`, `<=`, `>=`
- **null checks**: `IS NULL`, `IS NOT NULL`
- **pattern matching**: `LIKE 'pattern'`, `NOT LIKE 'pattern'`, `ILIKE 'pattern'` (case-insensitive), `NOT ILIKE 'pattern'`
  - `%` matches any sequence of characters; `_` matches exactly one character
- **range check**: `BETWEEN low AND high`, `NOT BETWEEN low AND high` (bounds inclusive)
- **set membership**: `IN (subquery)`, `NOT IN (subquery)`
- **logical operators**: `AND`, `OR`
- **grouping**: parentheses to control evaluation order
- **column references**: bare names or table-qualified names (`table.column`)
- blob columns support only `=` and `<>`
- comparisons involving a `NULL` operand always evaluate to false (three-valued logic)
- `NOT IN` with a subquery that returns any `NULL` value also evaluates to false (SQL three-valued logic)

Examples:

```sql
WHERE id = 1
WHERE name = 'Ada' AND active = TRUE
WHERE val = 1 OR val = 9
WHERE a = 1 AND (b = 0 OR b = 1)
WHERE score >= 75
WHERE users.id = 1
WHERE name IS NULL
WHERE score IS NOT NULL
WHERE name LIKE 'A%'
WHERE name ILIKE 'ada%'
WHERE code NOT LIKE 'TMP_%'
WHERE score BETWEEN 50 AND 100
WHERE score NOT BETWEEN 50 AND 100
WHERE id NOT IN (SELECT blocked_id FROM blocklist)
```

## Query planner

Single-table `SELECT` queries go through a rule-based logical/physical planner before execution.

### How it works

1. The planner flattens `AND`-connected predicates into a list of atoms.
2. For each secondary index on the queried table it scores the index by counting how many leading columns are covered by equality predicates.
3. The index with the highest score is chosen. If no index scores above zero, the planner considers `ORDER BY` optimisation (step 4). If there is no `ORDER BY` either, a full scan is used.
4. If no WHERE index is chosen but an `ORDER BY` clause is present and a secondary index exists whose leading column matches the first `ORDER BY` term (same direction), the planner emits a `PhysicalIndexOrderedScan`. Rows arrive in B+ tree key order so no in-memory sort is needed.
5. An **index seek** builds a sort-preserving prefix key from the matching predicates and calls `SecondaryBPlusTree.RangeSeek`. Remaining (unmatched) predicates become a **post-filter** applied to each fetched row.
6. A **full scan** reads all rows and evaluates all predicates in memory.

### Example

```sql
CREATE INDEX idx_customer ON orders (customer_id);

-- Both predicates visible to planner.
-- customer_id = 42 matches the index (score = 1).
-- status = 'open' has no index match → becomes post-filter.
SELECT id FROM orders WHERE customer_id = 42 AND status = 'open';
```

### Current limitations

- only **equality** predicates score index coverage; range predicates (`<`, `>`, `<=`, `>=`) do not contribute to the leading-column score (they always become a post-filter or trigger a full scan)
- the `ORDER BY` index optimisation only applies when a WHERE clause does not already use a different index; mixed WHERE+ORDER BY index is not yet costed
- `JOIN` queries always use full scans on each side; the planner only optimises single-table paths
- `ORDER BY` index scan requires all sort terms to share the same direction (all ASC or all DESC); mixed-direction multi-column ORDER BY falls back to in-memory sort
- if multiple indexes tie on score, the first one returned by the catalog wins

## Deliberate limitations

- range predicates on secondary indexes fall back to a full scan (equality-only planner for now)
- single active transaction per engine instance; nested transactions are not supported
- `HAVING` supports only a single aggregate comparison; compound `HAVING` expressions are not yet parsed
- aggregate queries are not supported in `JOIN` paths
- scalar functions (`COALESCE`, `NULLIF`, `IFNULL`) are only supported in single-table `SELECT` paths; `JOIN` and aggregation paths will throw

These constraints are intentional. The goal is to keep the implementation close to the underlying page and catalog mechanics without hiding them behind a large abstraction layer.

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

- `WHERE` on non-primary-key columns performs a full table scan unless a secondary index covers the predicate
- no window functions or CTEs
