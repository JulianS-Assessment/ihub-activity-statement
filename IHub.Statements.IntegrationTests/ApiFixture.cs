using Dapper;
using IHub.Statements.Api.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IHub.Statements.IntegrationTests;

[SetUpFixture]
public sealed class ApiFixture
{
    private static WebApplicationFactory<Program> _factory = null!;

    public static HttpClient Client { get; private set; } = null!;

    public static string ConnectionString { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        ConnectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Statements")!;
        await Schema.ApplyAsync(ConnectionString);
        Client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    public static async Task TruncateAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync("TRUNCATE intermediary_activity");
    }
}
