using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Sqlity.Query;

namespace Sqlity.Ado;

public sealed class SqlityCommand : DbCommand
{
    private string _commandText = string.Empty;

    public SqlityCommand() { }

    public SqlityCommand(string commandText, SqlityConnection connection)
    {
        CommandText  = commandText;
        DbConnection = connection;
    }

    [AllowNull]
    public override string CommandText { get => _commandText; set => _commandText = value ?? string.Empty; }
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbParameterCollection DbParameterCollection { get; } = new SqlityParameterCollection();

    protected override DbParameter CreateDbParameter() => new SqlityParameter();

    public override void Cancel() { }
    public override void Prepare() { }

    public override int ExecuteNonQuery()
    {
        EnsureConnectionOpen();
        return GetEngine().Execute(ApplyParameters(CommandText)).RowsAffected;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteNonQuery());
    }

    public override object? ExecuteScalar()
    {
        EnsureConnectionOpen();
        var result = GetEngine().Execute(ApplyParameters(CommandText));
        if (result.Rows.Count == 0 || result.Columns.Count == 0)
            return null;
        return result.Rows[0][0];
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteScalar());
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        EnsureConnectionOpen();
        var result = GetEngine().Execute(ApplyParameters(CommandText));
        return new SqlityDataReader(result);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteDbDataReader(behavior));
    }

    // Substitutes named parameters (@name) with their SQL literal equivalents so the
    // query engine — which does not have its own parameter binding layer — can execute
    // the statement as a plain SQL string.  Longer names are replaced first to prevent
    // a partial match of e.g. @p1 inside @p10.
    private string ApplyParameters(string sql)
    {
        if (DbParameterCollection.Count == 0)
            return sql;

        var sorted = DbParameterCollection
            .Cast<DbParameter>()
            .OrderByDescending(p => p.ParameterName.Length);

        foreach (var param in sorted)
        {
            var placeholder = param.ParameterName.StartsWith('@')
                ? param.ParameterName
                : "@" + param.ParameterName;
            sql = sql.Replace(placeholder, ToSqlLiteral(param.Value), StringComparison.Ordinal);
        }

        return sql;
    }

    private static string ToSqlLiteral(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b         => b ? "TRUE" : "FALSE",
        string s       => $"'{s.Replace("'", "''", StringComparison.Ordinal)}'",
        long l         => l.ToString(CultureInfo.InvariantCulture),
        int i          => i.ToString(CultureInfo.InvariantCulture),
        short sh       => sh.ToString(CultureInfo.InvariantCulture),
        byte by        => by.ToString(CultureInfo.InvariantCulture),
        double d       => d.ToString("G17", CultureInfo.InvariantCulture),
        float f        => f.ToString("G9", CultureInfo.InvariantCulture),
        decimal dec    => dec.ToString(CultureInfo.InvariantCulture),
        DateTime dt    => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        DateOnly date  => $"'{date:yyyy-MM-dd}'",
        byte[] bytes   => $"X'{Convert.ToHexString(bytes)}'",
        _              => $"'{value}'"
    };

    private QueryEngine GetEngine() =>
        ((SqlityConnection)DbConnection!).Engine;

    private void EnsureConnectionOpen()
    {
        if (DbConnection?.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open before executing a command.");
    }
}
