using Microsoft.EntityFrameworkCore.Storage;
using Sqlity.Ado;
using System.Data.Common;

namespace Sqlity.EFCore;

public sealed class SqlityRelationalConnection(RelationalConnectionDependencies dependencies)
    : RelationalConnection(dependencies)
{
    protected override DbConnection CreateDbConnection() => new SqlityConnection(ConnectionString!);
}
