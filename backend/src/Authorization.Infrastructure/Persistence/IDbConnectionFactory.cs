using System.Data.Common;

namespace Authorization.Infrastructure.Persistence;

public interface IDbConnectionFactory
{
    string ConnectionString { get; }

    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}