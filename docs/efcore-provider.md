# EF Core provider

`Sqlity.EFCore` is a minimal EF Core 10 relational provider that delegates SQL execution to the existing `Sqlity.Ado` layer. It is designed for learning and correctness rather than production use.

## Usage

```csharp
using Microsoft.EntityFrameworkCore;
using Sqlity.EFCore;

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlity("demo.sqlity");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).HasColumnType("INT64").ValueGeneratedNever();
            b.Property(u => u.Name).HasColumnType("STRING");
            b.Property(u => u.Score).HasColumnType("INT64");
        });
    }
}

public class User
{
    public long Id    { get; set; }
    public string Name  { get; set; } = string.Empty;
    public long Score { get; set; }
}
```

```csharp
using var ctx = new AppDbContext();
ctx.Database.EnsureCreated();

ctx.Users.Add(new User { Id = 1, Name = "Ada", Score = 42 });
ctx.SaveChanges();

var top = ctx.Users.Where(u => u.Score > 10).OrderBy(u => u.Name).ToList();
```

## Registered services

| Interface | Implementation |
|-----------|---------------|
| `IDatabaseProvider` | `DatabaseProvider<SqlityOptionsExtension>` |
| `IProviderConventionSetBuilder` | `SqlityConventionSetBuilder` |
| `IRelationalConnection` | `SqlityRelationalConnection` |
| `IRelationalDatabaseCreator` | `SqlityDatabaseCreator` |
| `IRelationalTypeMappingSource` | `SqlityTypeMappingSource` |
| `ISqlGenerationHelper` | `SqlitySqlGenerationHelper` |
| `IUpdateSqlGenerator` | `SqlityUpdateSqlGenerator` |
| `IModificationCommandBatchFactory` | `SqlityModificationCommandBatchFactory` |
| `IMigrationsSqlGenerator` | `SqlityMigrationsSqlGenerator` |
| `IHistoryRepository` | `SqlityHistoryRepository` |
| `IQuerySqlGeneratorFactory` | `SqlityQuerySqlGeneratorFactory` |

## Type mapping

| .NET type | Sqlity column type |
|-----------|-------------------|
| `string` | `STRING` |
| `long` | `INT64` |
| `int` | `INT64` |
| `bool` | `BOOLEAN` |
| `double` | `REAL` |
| `float` | `REAL` |
| `decimal` | `REAL` (lossy — no decimal type in Sqlity) |
| `DateTime` | `DATETIME` |

## Key decisions

### No RETURNING clause

Sqlity's SQL parser does not support `RETURNING`. `SqlityUpdateSqlGenerator` overrides `AppendInsertOperation`, `AppendUpdateOperation`, and `AppendDeleteOperation` to emit plain `INSERT`/`UPDATE`/`DELETE` without a returning clause, and returns `ResultSetMapping.NoResults` for each. This means EF Core does not perform optimistic-concurrency row-count checks on UPDATE and DELETE.

### No auto-increment keys

Because there is no RETURNING, store-generated primary keys cannot be read back after INSERT. Always configure PK properties with `.ValueGeneratedNever()` and supply IDs explicitly.

### LIMIT/OFFSET pagination

EF Core's default relational query generator produces `OFFSET … ROWS FETCH NEXT … ROWS ONLY` (SQL Server style). `SqlityQuerySqlGenerator` overrides `GenerateLimitOffset` to emit `LIMIT … OFFSET …`, which is what Sqlity's parser expects.

### Unquoted identifiers

Sqlity's lexer does not handle double-quoted or bracket-quoted identifiers. `SqlitySqlGenerationHelper` overrides all `DelimitIdentifier` overloads to return identifiers without any quoting.

### EnsureCreated / EnsureDeleted

`SqlityDatabaseCreator` implements the three operations needed for schema management:

- `EnsureCreated()` — creates the file and runs `CREATE TABLE` DDL for all entity types if the database does not already exist.
- `EnsureDeleted()` — deletes the database file if it exists.
- `Exists()` — checks whether the `.sqlity` file is present on disk.

Full migrations (`dotnet ef migrations`) are not supported in this release.

## Known limitations

- No auto-generated (store-side) primary keys.
- No optimistic-concurrency (`RowVersion` / `ConcurrencyToken`) support.
- No migrations — use `EnsureCreated` / `EnsureDeleted` only.
- `decimal` maps to `REAL` (floating-point, lossy).
- Only the SQL subset supported by Sqlity's query engine is available (no subqueries in projections, no window functions, no CTEs).
