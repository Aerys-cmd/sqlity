using Microsoft.EntityFrameworkCore.Query;

namespace Sqlity.EFCore;

public sealed class SqlityQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create() => new SqlityQuerySqlGenerator(dependencies);
}
