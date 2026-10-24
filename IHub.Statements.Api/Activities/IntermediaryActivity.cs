namespace IHub.Statements.Api.Activities;

// OccurredAt is nullable so a missing field reaches validation as null rather than as DateTimeOffset.MinValue.
public sealed record IntermediaryActivity(string Source, string ActivityId, decimal Amount, string Currency, DateTimeOffset? OccurredAt);
