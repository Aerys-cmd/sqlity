using System.Text;
using Microsoft.EntityFrameworkCore.Update;

namespace Sqlity.EFCore;

/// <summary>
/// Sqlity does not support the RETURNING clause. This generator overrides all
/// three DML operations to emit plain INSERT/UPDATE/DELETE without RETURNING and
/// returns <see cref="ResultSetMapping.NoResults"/> so the batch consumer does
/// not attempt to read a result set.
/// </summary>
public sealed class SqlityUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
    public override ResultSetMapping AppendInsertOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        var writeOperations = command.ColumnModifications.Where(o => o.IsWrite).ToList();
        AppendInsertCommand(commandStringBuilder, command.TableName, command.Schema,
            writeOperations, readOperations: []);
        requiresTransaction = false;
        return ResultSetMapping.NoResults;
    }

    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        var writeOperations = command.ColumnModifications.Where(o => o.IsWrite).ToList();
        var conditionOperations = command.ColumnModifications.Where(o => o.IsCondition).ToList();
        AppendUpdateCommand(commandStringBuilder, command.TableName, command.Schema,
            writeOperations, readOperations: [], conditionOperations,
            appendReturningOneClause: false);
        requiresTransaction = false;
        return ResultSetMapping.NoResults;
    }

    public override ResultSetMapping AppendDeleteOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        var conditionOperations = command.ColumnModifications.Where(o => o.IsCondition).ToList();
        AppendDeleteCommand(commandStringBuilder, command.TableName, command.Schema,
            readOperations: [], conditionOperations,
            appendReturningOneClause: false);
        requiresTransaction = false;
        return ResultSetMapping.NoResults;
    }
}
