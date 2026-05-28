using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Sqlity.EFCore;

public static class SqlityDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Configures the <see cref="DbContext"/> to use Sqlity with the specified database file path.
    /// </summary>
    public static DbContextOptionsBuilder UseSqlity(
        this DbContextOptionsBuilder optionsBuilder,
        string filePath,
        Action<RelationalDbContextOptionsBuilder<SqlityDbContextOptionsBuilder, SqlityOptionsExtension>>? sqlityOptionsAction = null)
    {
        var extension = (SqlityOptionsExtension)GetOrCreateExtension(optionsBuilder)
            .WithConnectionString($"Data Source={filePath}")
            .WithMaxBatchSize(1);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        sqlityOptionsAction?.Invoke(new SqlityDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    private static SqlityOptionsExtension GetOrCreateExtension(DbContextOptionsBuilder optionsBuilder)
    {
        var existing = optionsBuilder.Options.FindExtension<SqlityOptionsExtension>();
        return existing ?? new SqlityOptionsExtension();
    }
}

// Thin typed builder so the options action lambda is properly typed
public sealed class SqlityDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<SqlityDbContextOptionsBuilder, SqlityOptionsExtension>(optionsBuilder)
{
}
