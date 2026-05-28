using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sqlity.EFCore;

/// <summary>
/// Minimal migrations history repository for Sqlity.
/// Full migrations support is out of scope; this implementation satisfies the
/// DI container and provides the SQL fragments required by EF Core's migration
/// infrastructure.
/// </summary>
public sealed class SqlityHistoryRepository(HistoryRepositoryDependencies dependencies)
    : HistoryRepository(dependencies)
{
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    protected override string ExistsSql =>
        $"SELECT 1 FROM {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} LIMIT 1";

    protected override bool InterpretExistsResult(object? value) => value != null && value != DBNull.Value;

    public override IMigrationsDatabaseLock AcquireDatabaseLock() => new NoOpDatabaseLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationsDatabaseLock>(new NoOpDatabaseLock(this));

    public override string GetCreateScript() =>
        $"CREATE TABLE {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} " +
        $"(MigrationId STRING NOT NULL PRIMARY KEY, ProductVersion STRING NOT NULL);";

    public override string GetCreateIfNotExistsScript() => GetCreateScript();

    public override string GetInsertScript(HistoryRow row) =>
        $"INSERT INTO {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} " +
        $"(MigrationId, ProductVersion) VALUES " +
        $"('{Escape(row.MigrationId)}', '{Escape(row.ProductVersion)}');";

    public override string GetDeleteScript(string migrationId) =>
        $"DELETE FROM {SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema)} " +
        $"WHERE MigrationId = '{Escape(migrationId)}';";

    public override string GetBeginIfExistsScript(string migrationId) => string.Empty;
    public override string GetBeginIfNotExistsScript(string migrationId) => string.Empty;
    public override string GetEndIfScript() => string.Empty;

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class NoOpDatabaseLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => historyRepository;

        public void ReacquireIfNeeded(bool requiresSnapshot, bool? requiresLock) { }

        public Task ReacquireIfNeededAsync(bool requiresSnapshot, bool? requiresLock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }
}
