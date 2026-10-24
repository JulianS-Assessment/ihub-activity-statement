using System.Diagnostics.Metrics;

namespace IHub.Statements.Api.Observability;

public sealed class IngestionMetrics
{
    public const string MeterName = "IHub.Statements";

    private readonly Counter<long> _ingested;
    private readonly Histogram<double> _sourceLateness;

    public IngestionMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _ingested = meter.CreateCounter<long>("ihub.activities.ingested", description: "Posted activities by outcome: created, duplicate, conflict or invalid.");
        _sourceLateness = meter.CreateHistogram<double>("ihub.activity.source_lateness", "s", "From occurredAt to the row being stored; large for legitimately late facts.");
    }

    public void Ingested(string outcome) => _ingested.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    // Validation allows occurredAt up to five minutes ahead; a lateness histogram records that skew as zero.
    public void Stored(TimeSpan sourceLateness) => _sourceLateness.Record(Math.Max(0, sourceLateness.TotalSeconds));
}
