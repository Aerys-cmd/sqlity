using Microsoft.EntityFrameworkCore.Update;

namespace Sqlity.EFCore;

public sealed class SqlityModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize: 1);

public sealed class SqlityModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies)
    : IModificationCommandBatchFactory
{
    public ModificationCommandBatch Create()
        => new SqlityModificationCommandBatch(dependencies);
}

