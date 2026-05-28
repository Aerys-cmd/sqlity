using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Sqlity.EFCore;

/// <summary>
/// Generates SQL using Sqlity-compatible LIMIT/OFFSET pagination instead of the
/// default "OFFSET x ROWS FETCH NEXT y ROWS ONLY" syntax.
/// </summary>
public sealed class SqlityQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit is null && selectExpression.Offset is null)
            return;

        Sql.AppendLine().Append("LIMIT ");

        if (selectExpression.Limit is not null)
            Visit(selectExpression.Limit);
        else
            Sql.Append("-1");

        if (selectExpression.Offset is not null)
        {
            Sql.Append(" OFFSET ");
            Visit(selectExpression.Offset);
        }
    }
}
