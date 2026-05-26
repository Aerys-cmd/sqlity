using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
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
        return GetEngine().Execute(CommandText).RowsAffected;
    }

    public override object? ExecuteScalar()
    {
        EnsureConnectionOpen();
        var result = GetEngine().Execute(CommandText);
        if (result.Rows.Count == 0 || result.Columns.Count == 0)
            return null;
        return result.Rows[0][0];
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        EnsureConnectionOpen();
        var result = GetEngine().Execute(CommandText);
        return new SqlityDataReader(result);
    }

    private QueryEngine GetEngine() =>
        ((SqlityConnection)DbConnection!).Engine;

    private void EnsureConnectionOpen()
    {
        if (DbConnection?.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open before executing a command.");
    }
}
