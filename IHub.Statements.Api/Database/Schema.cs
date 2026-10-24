using Dapper;
using Npgsql;

namespace IHub.Statements.Api.Database;

public static class Schema
{
    public static async Task ApplyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql")));
    }
}
