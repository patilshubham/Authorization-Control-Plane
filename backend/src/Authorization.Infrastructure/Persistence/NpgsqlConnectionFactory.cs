using System.Data.Common;
using Npgsql;

namespace Authorization.Infrastructure.Persistence;

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly NpgsqlDataSource dataSource;

    public NpgsqlConnectionFactory(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public string ConnectionString => dataSource.ConnectionString;

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await dataSource.OpenConnectionAsync(cancellationToken);
    }
}