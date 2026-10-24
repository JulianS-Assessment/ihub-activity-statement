using Dapper;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace IHub.Statements.Api.Statements;

public sealed class StatementEndpoint(NpgsqlDataSource dataSource)
{
    private const string SelectStatement = """
        SELECT coalesce(sum(amount), 0), max(received_at)
        FROM intermediary_activity
        WHERE intermediary_id = @IntermediaryId AND occurred_at >= @From AND occurred_at < @To
        """;

    public async Task<Results<Ok<StatementResponse>, ValidationProblem>> GetAsync(StatementRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        Validation.RequireIdentifier(errors, "intermediaryId", request.IntermediaryId);
        var fromUtc = Validation.RequireDateTimeOffset(errors, "from", request.From);
        var toUtc = Validation.RequireDateTimeOffset(errors, "to", request.To);
        if (fromUtc >= toUtc)
        {
            errors["from"] = ["Must be before to."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var intermediaryId = request.IntermediaryId.Trim();
        var window = (From: fromUtc!.Value, To: toUtc!.Value);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var (grossTotal, lastUpdatedAt) = await connection.QuerySingleAsync<(decimal GrossTotal, DateTime? LastUpdatedAt)>(new CommandDefinition(
            SelectStatement,
            new { IntermediaryId = intermediaryId, window.From, window.To },
            cancellationToken: cancellationToken));

        return TypedResults.Ok(new StatementResponse(intermediaryId, window.From, window.To, grossTotal, "ZAR", lastUpdatedAt is { } at ? new DateTimeOffset(at) : null));
    }
}
