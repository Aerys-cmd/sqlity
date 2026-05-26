using System.Data;
using System.Data.Common;

namespace Sqlity.Ado;

public sealed class SqlityTransaction : DbTransaction
{
    private readonly SqlityConnection _connection;

    internal SqlityTransaction(SqlityConnection connection)
    {
        _connection = connection;
    }

    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
    protected override DbConnection DbConnection => _connection;

    public override void Commit()
        => throw new NotSupportedException("Transactions are not yet supported. See Roadmap §3.");

    public override void Rollback()
        => throw new NotSupportedException("Transactions are not yet supported. See Roadmap §3.");
}
