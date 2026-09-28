using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Authorization.Infrastructure.Persistence;

public sealed class AuthorizationDbContextFactory : IDesignTimeDbContextFactory<AuthorizationDbContext>
{
    public AuthorizationDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=authorization;Username=authorization;Password=authorization_dev_password";

        var optionsBuilder = new DbContextOptionsBuilder<AuthorizationDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
            npgsqlOptions.MigrationsAssembly(typeof(AuthorizationDbContext).Assembly.FullName));

        return new AuthorizationDbContext(optionsBuilder.Options);
    }
}