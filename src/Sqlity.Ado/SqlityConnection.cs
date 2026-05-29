using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Sqlity.Query;

namespace Sqlity.Ado;

public sealed class SqlityConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private QueryEngine? _engine;
    private SqlityTransaction? _activeTransaction;

    public SqlityConnection() { }

    public SqlityConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    internal QueryEngine Engine =>
        _engine ?? throw new InvalidOperationException("Connection is not open.");

    [AllowNull]
    public override string ConnectionString { get => _connectionString; set => _connectionString = value ?? string.Empty; }
    public override string Database => Path.GetFileNameWithoutExtension(DataSource);
    public override string DataSource => ParseDataSource(ConnectionString);
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void Open()
    {
        if (_state == ConnectionState.Open)
            return;

        _state = ConnectionState.Connecting;
        try
        {
            _engine = new QueryEngine(DataSource);
            _state = ConnectionState.Open;
        }
        catch
        {
            _state = ConnectionState.Closed;
            throw;
        }
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public override void Close()
    {
        // Roll back any active transaction before closing so the database file is left consistent.
        if (_activeTransaction is not null && _engine is not null && _engine.InTransaction)
        {
            try { _engine.Rollback(); } catch { /* best effort */ }
        }

        _activeTransaction = null;
        _engine?.Dispose();
        _engine = null;
        _state = ConnectionState.Closed;
    }

    public override void ChangeDatabase(string databaseName)
        => throw new NotSupportedException("Sqlity does not support changing the database on an open connection.");

    protected override DbCommand CreateDbCommand() =>
        new SqlityCommand(string.Empty, this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_activeTransaction is not null)
            throw new InvalidOperationException("A transaction is already active on this connection.");

        Engine.BeginTransaction();
        _activeTransaction = new SqlityTransaction(this);
        return _activeTransaction;
    }

    internal void ClearActiveTransaction() => _activeTransaction = null;

    /// <summary>
    /// Returns the names of all user-defined tables in the open database.
    /// </summary>
    public IReadOnlyList<string> ListTableNames() => Engine.ListTables();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }

    private static string ParseDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return string.Empty;

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (builder.TryGetValue("Data Source", out var value))
                return value.ToString()!;
        }
        catch
        {
            // If the string isn't key=value format, treat it as a bare file path
        }

        return connectionString;
    }
}
