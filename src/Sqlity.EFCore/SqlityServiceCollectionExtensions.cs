using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sqlity.EFCore;

public static class SqlityServiceCollectionExtensions
{
    public static IServiceCollection AddEntityFrameworkSqlity(this IServiceCollection services)
    {
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<IDatabaseProvider, DatabaseProvider<SqlityOptionsExtension>>()
            .TryAdd<IProviderConventionSetBuilder, SqlityConventionSetBuilder>()
            .TryAdd<IRelationalConnection, SqlityRelationalConnection>()
            .TryAdd<IRelationalDatabaseCreator, SqlityDatabaseCreator>()
            .TryAdd<IRelationalTypeMappingSource, SqlityTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, SqlitySqlGenerationHelper>()
            .TryAdd<IUpdateSqlGenerator, SqlityUpdateSqlGenerator>()
            .TryAdd<IModificationCommandBatchFactory, SqlityModificationCommandBatchFactory>()
            .TryAdd<IMigrationsSqlGenerator, SqlityMigrationsSqlGenerator>()
            .TryAdd<IHistoryRepository, SqlityHistoryRepository>()
            .TryAdd<IQuerySqlGeneratorFactory, SqlityQuerySqlGeneratorFactory>()
            .TryAddCoreServices();

        // Register provider-specific LoggingDefinitions directly because the builder's
        // generic TryAdd constraint doesn't accept subclasses of RelationalLoggingDefinitions.
        services.TryAddSingleton<LoggingDefinitions, SqlityLoggingDefinitions>();

        return services;
    }
}
