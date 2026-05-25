# Sqlity.Cli

`Sqlity.Cli` is the tiny runnable sample for opening a `.sqlity` file and executing one SQL statement at a time.

These examples are generated from executable tests so the documented command output stays in sync with the sample.

To refresh this file:

```bash
SQLITY_UPDATE_EXECUTABLE_EXAMPLES=1 dotnet test tests/Sqlity.Cli.Tests/Sqlity.Cli.Tests.csproj
```

Each command below reopens the same `demo.sqlity` file, so the workflow mirrors normal CLI usage.

## Create a table

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);"
```

```text
Rows affected: 0
```

## Insert a row

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "INSERT INTO users VALUES (1, 'Ada', TRUE);"
```

```text
Rows affected: 1
```

## Insert with a reordered column list

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);"
```

```text
Rows affected: 1
```

## Select a projection by primary key

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "SELECT id, name FROM users WHERE id = 2;"
```

```text
id | name
2 | Linus
(1 row(s))
```

## Update a row

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;"
```

```text
Rows affected: 1
```

## Delete a row

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "DELETE FROM users WHERE id = 2;"
```

```text
Rows affected: 1
```

## Create a table with a BLOB column

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);"
```

```text
Rows affected: 0
```

## Insert a BLOB literal

```bash
dotnet run --project samples/Sqlity.Cli -- demo.sqlity "INSERT INTO files VALUES (1, 'spec', X'CAFE');"
```

```text
Rows affected: 1
```

## Pipe SQL through standard input

```bash
echo "SELECT name, payload FROM files WHERE id = 1;" | dotnet run --project samples/Sqlity.Cli -- demo.sqlity
```

```text
name | payload
spec | X'CAFE'
(1 row(s))
```

