using Microsoft.EntityFrameworkCore;
using Sqlity.EFCore;

namespace Sqlity.EFCore.Tests;

// ── Shared model ─────────────────────────────────────────────────────────────

public class User
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnType("INT64");
            e.Property(u => u.Name).HasColumnType("STRING");
            e.Property(u => u.Score).HasColumnType("INT64");
        });
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

file static class TempDb
{
    public static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"sqlity_efcore_test_{Guid.NewGuid():N}.sqlity");

    public static TestDbContext Open(string path)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        builder.UseSqlity(path);
        return new TestDbContext(builder.Options);
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class EfCoreProviderTests
{
    [Fact]
    public void EnsureCreated_CreatesSchemaAndReturnsTrue()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            var created = ctx.Database.EnsureCreated();

            Assert.True(created);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            ctx.Database.EnsureCreated();
            var second = ctx.Database.EnsureCreated();

            // second call returns false because the schema already exists
            Assert.False(second);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EnsureDeleted_RemovesFile()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            ctx.Database.EnsureCreated();
            Assert.True(File.Exists(path));

            ctx.Database.EnsureDeleted();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BasicCrud_AddFindRemove()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            ctx.Database.EnsureCreated();

            ctx.Users.Add(new User { Id = 1, Name = "Alice", Score = 10 });
            ctx.SaveChanges();

            var found = ctx.Users.Find(1L);
            Assert.NotNull(found);
            Assert.Equal("Alice", found!.Name);
            Assert.Equal(10, found.Score);

            ctx.Users.Remove(found);
            ctx.SaveChanges();

            var afterDelete = ctx.Users.Find(1L);
            Assert.Null(afterDelete);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LinqQuery_WhereFilter_ReturnsMatchingRows()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            ctx.Database.EnsureCreated();

            ctx.Users.AddRange(
                new User { Id = 1, Name = "Alice", Score = 3 },
                new User { Id = 2, Name = "Bob",   Score = 7 },
                new User { Id = 3, Name = "Carol",  Score = 9 }
            );
            ctx.SaveChanges();

            var highScorers = ctx.Users.Where(u => u.Score > 5).OrderBy(u => u.Id).ToList();
            Assert.Equal(2, highScorers.Count);
            Assert.Equal("Bob",   highScorers[0].Name);
            Assert.Equal("Carol", highScorers[1].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Update_ModifiesExistingRow()
    {
        var path = TempDb.NewPath();
        try
        {
            using var ctx = TempDb.Open(path);
            ctx.Database.EnsureCreated();

            ctx.Users.Add(new User { Id = 1, Name = "Alice", Score = 5 });
            ctx.SaveChanges();

            var user = ctx.Users.Find(1L)!;
            user.Score = 99;
            ctx.SaveChanges();

            ctx.ChangeTracker.Clear();
            var updated = ctx.Users.Find(1L)!;
            Assert.Equal(99, updated.Score);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
