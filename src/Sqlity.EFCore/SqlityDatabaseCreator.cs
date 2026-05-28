using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Sqlity.Ado;

namespace Sqlity.EFCore;

public sealed class SqlityDatabaseCreator(
    RelationalDatabaseCreatorDependencies dependencies,
    IRelationalConnection connection)
    : RelationalDatabaseCreator(dependencies)
{
    private string FilePath => GetFilePath();

    private string GetFilePath()
    {
        var connStr = connection.ConnectionString ?? string.Empty;
        // Parse "Data Source=<path>" (case-insensitive)
        foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                return trimmed["Data Source=".Length..].Trim();
        }
        return connStr;
    }

    public override bool Exists() => File.Exists(FilePath);

    public override void Create()
    {
        // Sqlity auto-creates the file on first Open(); nothing to do here.
    }

    public override void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    public override bool HasTables()
    {
        connection.Open();
        try
        {
            var sqlityConn = (SqlityConnection)(connection.DbConnection
                ?? throw new InvalidOperationException("No underlying DbConnection."));
            return sqlityConn.ListTableNames().Count > 0;
        }
        finally
        {
            connection.Close();
        }
    }

    // Generate DDL directly from model annotations, deliberately avoiding
    // GetRelationalModel() / IDesignTimeModel.Model.  EF Core 10 uses a
    // RuntimeModel wrapper whose entity types share their annotation stores with
    // the underlying design-time Model, so calling GetRelationalModel() on
    // either IDesignTimeModel.Model or ctx.Model and then later letting
    // SaveChanges() call it on the other results in a duplicate-annotation
    // exception.  We call GetRelationalModel() on ctx.Model exactly once here
    // to initialize the TableMappings/ColumnMappings runtime annotations that
    // EF Core 10's SaveChanges() requires, then generate DDL directly from the
    // design-time annotations — never touching IDesignTimeModel.Model.
    public override void CreateTables()
    {
        var model = Dependencies.CurrentContext.Context.Model;

        // Populate the runtime TableMappings/ColumnMappings annotations so that
        // SaveChanges() can resolve table/column lookups later.  Using ctx.Model
        // ensures only this single model instance is ever initialized; calling
        // GetRelationalModel() on IDesignTimeModel.Model as well would trigger
        // the "duplicate annotation" exception on shared property stores.
        _ = model.GetRelationalModel();

        connection.Open();
        try
        {
            foreach (var entityType in model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (tableName == null) continue;

                var pk = entityType.FindPrimaryKey()?.Properties.ToHashSet()
                         ?? [];

                var cols = entityType.GetProperties().Select(p =>
                {
                    var colName = p.GetColumnName() ?? p.Name;
                    var colType = p.GetColumnType() ?? MapClrType(p.ClrType);
                    var nullable = p.IsNullable ? "" : " NOT NULL";
                    var pkMark = pk.Contains(p) ? " PRIMARY KEY" : "";
                    return $"{colName} {colType}{nullable}{pkMark}";
                });

                var ddl = $"CREATE TABLE {tableName} ({string.Join(", ", cols)})";
                using var dbCmd = connection.DbConnection!.CreateCommand();
                dbCmd.CommandText = ddl;
                dbCmd.ExecuteNonQuery();
            }
        }
        finally
        {
            connection.Close();
        }
    }

    private static string MapClrType(Type? clrType) => clrType switch
    {
        _ when clrType == typeof(string)   => "STRING",
        _ when clrType == typeof(long)     => "INT64",
        _ when clrType == typeof(int)      => "INT64",
        _ when clrType == typeof(short)    => "INT64",
        _ when clrType == typeof(bool)     => "BOOLEAN",
        _ when clrType == typeof(double)   => "REAL",
        _ when clrType == typeof(float)    => "REAL",
        _ when clrType == typeof(decimal)  => "REAL",
        _ when clrType == typeof(DateTime) => "DATETIME",
        _ when clrType == typeof(DateOnly) => "DATE",
        _                                  => "STRING"
    };
}

