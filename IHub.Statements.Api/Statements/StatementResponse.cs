namespace IHub.Statements.Api.Statements;

// LastUpdatedAt is the latest received_at among the window's rows, null for an empty window. Two overlapping
// inserts can commit in the other order, so it is the latest arrival, not the moment the total last changed.
public sealed record StatementResponse(string IntermediaryId, DateTimeOffset From, DateTimeOffset To, decimal GrossTotal, string Currency, DateTimeOffset? LastUpdatedAt);
