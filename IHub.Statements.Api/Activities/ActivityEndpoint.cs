using Dapper;
using IHub.Statements.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace IHub.Statements.Api.Activities;

public sealed class ActivityEndpoint(NpgsqlDataSource dataSource, TimeProvider timeProvider, IngestionMetrics metrics, ILogger<ActivityEndpoint> logger)
{
    private const string InsertActivity = """
        INSERT INTO intermediary_activity (source, activity_id, intermediary_id, amount, currency, occurred_at)
        VALUES (@Source, @ActivityId, @IntermediaryId, @Amount, @Currency, @OccurredAt)
        ON CONFLICT (source, activity_id) DO NOTHING
        """;

    private const string SelectExistingActivity = """
        SELECT (intermediary_id, amount, currency, occurred_at) = (@IntermediaryId, @Amount, @Currency, @OccurredAt)
        FROM intermediary_activity WHERE source = @Source AND activity_id = @ActivityId
        """;

    // numeric(19,4) holds at most 15 integer digits.
    private const decimal AmountLimit = 1_000_000_000_000_000m;

    public async Task<Results<Created, Ok, ValidationProblem, ProblemHttpResult>> PostAsync(IngestActivityRequest request, CancellationToken cancellationToken)
    {
        var errors = ValidateRequest(request, timeProvider.GetUtcNow());
        if (errors.Count > 0)
        {
            metrics.Ingested("invalid");
            LogOutcome(LogLevel.Warning, "invalid", request);
            return TypedResults.ValidationProblem(errors);
        }

        request = request.WithTrimmedIds();
        var activity = request.Activity;
        // Npgsql writes a DateTimeOffset only at offset zero.
        var occurredAt = activity.OccurredAt!.Value.ToUniversalTime();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertActivity,
            new
            {
                activity.Source,
                activity.ActivityId,
                request.IntermediaryId,
                activity.Amount,
                activity.Currency,
                OccurredAt = occurredAt
            },
            cancellationToken: cancellationToken));
        if (inserted == 1)
        {
            metrics.Ingested("created");
            LogOutcome(LogLevel.Information, "created", request);
            metrics.Stored(timeProvider.GetUtcNow() - occurredAt);
            return TypedResults.Created();
        }

        var identical = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            SelectExistingActivity,
            new { activity.Source, activity.ActivityId, request.IntermediaryId, activity.Amount, activity.Currency, OccurredAt = occurredAt },
            cancellationToken: cancellationToken));

        if (identical)
        {
            metrics.Ingested("duplicate");
            LogOutcome(LogLevel.Information, "duplicate", request);
            return TypedResults.Ok();
        }

        metrics.Ingested("conflict");
        LogOutcome(LogLevel.Warning, "conflict", request);
        return TypedResults.Problem(title: "An activity with this source and id already exists with different content.", statusCode: StatusCodes.Status409Conflict);
    }

    // The trace id is not an argument: the hosting scope puts it on every line.
    private void LogOutcome(LogLevel level, string outcome, IngestActivityRequest request)
    {
        if (logger.IsEnabled(level))
        {
            logger.Log(level, "Activity {Outcome} {Source} {ActivityId}", outcome, request.Activity.Source?.Trim(), request.Activity.ActivityId?.Trim());
        }
    }

    private static Dictionary<string, string[]> ValidateRequest(IngestActivityRequest request, DateTimeOffset now)
    {
        var activity = request.Activity;
        var errors = new Dictionary<string, string[]>();
        Validation.RequireIdentifier(errors, "intermediaryId", request.IntermediaryId);
        Validation.RequireIdentifier(errors, "source", activity.Source);
        Validation.RequireIdentifier(errors, "activityId", activity.ActivityId);

        Validation.Report(errors, "amount", activity.Amount switch
        {
            0 => "Must not be zero.",
            var a when decimal.Round(a, 4) != a => "Must have at most four decimal places.",
            var a when Math.Abs(a) >= AmountLimit => "Must be below one quadrillion in absolute value.",
            _ => null
        });

        Validation.Report(errors, "currency", activity.Currency == "ZAR" ? null : "Must be ZAR.");

        Validation.Report(errors, "occurredAt", activity.OccurredAt switch
        {
            null => "Is required.",
            var at when at > now.AddMinutes(5) => "Must not be more than five minutes in the future.",
            _ => null
        });

        return errors;
    }
}
