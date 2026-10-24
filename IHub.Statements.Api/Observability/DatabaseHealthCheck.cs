using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace IHub.Statements.Api.Observability;

// A failure throws, and the health check service reports a thrown check as Unhealthy (503).
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
