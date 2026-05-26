using System.Data;
using System.Data.Common;

namespace Sqlity.Ado;

public sealed class SqlityTransaction : DbTransaction
{
    private readonly SqlityConnection _connection;
    private bool _completed;

    internal SqlityTransaction(SqlityConnection connection)
    {
        _connection = connection;
    }

    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        if (_completed)
            throw new InvalidOperationException("This transaction has already been committed or rolled back.");

        _completed = true;
        _connection.Engine.Commit();
        _connection.ClearActiveTransaction();
    }

    public override void Rollback()
    {
        if (_completed)
            throw new InvalidOperationException("This transaction has already been committed or rolled back.");

        _completed = true;
        _connection.Engine.Rollback();
        _connection.ClearActiveTransaction();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            // Roll back automatically if the transaction was never committed or rolled back.
            _completed = true;
            _connection.Engine.Rollback();
            _connection.ClearActiveTransaction();
        }

        base.Dispose(disposing);
    }
}
