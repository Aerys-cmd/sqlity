using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text;

namespace Sqlity.EFCore;

/// <summary>
/// Generates Sqlity-compatible DDL.  Sqlity only supports inline PRIMARY KEY
/// declarations (<c>col TYPE PRIMARY KEY</c>) so we diverge from standard SQL here.
/// </summary>
public sealed class SqlityMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies)
    : MigrationsSqlGenerator(dependencies)
{
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("CREATE TABLE ");
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        builder.AppendLine(" (");

        var columnLines = new List<string>();
        foreach (var col in operation.Columns)
        {
            var sb = new StringBuilder("    ");
            sb.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(col.Name));
            sb.Append(' ');
            sb.Append(col.ColumnType ?? MapClrType(col.ClrType));

            if (!col.IsNullable)
                sb.Append(" NOT NULL");

            // Sqlity requires inline PRIMARY KEY
            if (operation.PrimaryKey?.Columns.Contains(col.Name) == true)
                sb.Append(" PRIMARY KEY");

            if (col.DefaultValueSql is { } defSql)
            {
                sb.Append(" DEFAULT ");
                sb.Append(defSql);
            }
            else if (col.DefaultValue is { } defVal)
            {
                sb.Append(" DEFAULT ");
                sb.Append(ToSqlLiteral(defVal));
            }

            columnLines.Add(sb.ToString());
        }

        builder.Append(string.Join(",\n", columnLines));
        builder.AppendLine();
        builder.Append(")");

        if (terminate)
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

        builder.EndCommand();
    }

    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        builder.Append("DROP TABLE ");
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));

        if (terminate)
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

        builder.EndCommand();
    }

    private static string MapClrType(Type? clrType) => clrType switch
    {
        _ when clrType == typeof(string)   => "STRING",
        _ when clrType == typeof(long)     => "INT64",
        _ when clrType == typeof(int)      => "INT64",
        _ when clrType == typeof(short)    => "INT64",
        _ when clrType == typeof(byte)     => "INT64",
        _ when clrType == typeof(bool)     => "BOOLEAN",
        _ when clrType == typeof(double)   => "REAL",
        _ when clrType == typeof(float)    => "REAL",
        _ when clrType == typeof(decimal)  => "REAL",
        _ when clrType == typeof(DateTime) => "DATETIME",
        _ when clrType == typeof(DateOnly) => "DATE",
        _                                  => "STRING"
    };

    private static string ToSqlLiteral(object value) => value switch
    {
        null or DBNull => "NULL",
        bool b         => b ? "TRUE" : "FALSE",
        string s       => $"'{s.Replace("'", "''")}'",
        _              => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
    };
}
