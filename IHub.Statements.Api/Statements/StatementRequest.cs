namespace IHub.Statements.Api.Statements;

// Query binding reads a DateTimeOffset without an offset as well, so the bounds arrive as strings and get the same
// explicit-offset rule as occurredAt: a broker's local midnight read as UTC would shift the window by two hours.
public sealed record StatementRequest(string IntermediaryId, string? From, string? To);
