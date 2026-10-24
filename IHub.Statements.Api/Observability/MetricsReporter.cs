using System.Diagnostics.Metrics;
using System.Text.Json;

namespace IHub.Statements.Api.Observability;

// Local viewing without an exporter: totals since start, written as one log line per interval. The JSON console
// formatter writes a non-primitive property through ToString, so the snapshot goes out as a JSON string.
public sealed class MetricsReporter(ILogger<MetricsReporter> logger, IConfiguration configuration) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _counters = [];
    private readonly Dictionary<string, (long Count, double Sum, double Max)> _histograms = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == IngestionMetrics.MeterName
                    || instrument is { Meter.Name: "Microsoft.AspNetCore.Hosting", Name: "http.server.request.duration" })
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = Key(instrument, tags);
            lock (_gate)
            {
                _counters[key] = _counters.GetValueOrDefault(key) + value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var key = Key(instrument, tags);
            lock (_gate)
            {
                var (count, sum, max) = _histograms.GetValueOrDefault(key, (0, 0, double.MinValue));
                _histograms[key] = (count + 1, sum + value, Math.Max(max, value));
            }
        });
        listener.Start();

        using var timer = new PeriodicTimer(configuration.GetValue("Metrics:ReportInterval", TimeSpan.FromSeconds(30)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                string snapshot;
                lock (_gate)
                {
                    snapshot = JsonSerializer.Serialize(_counters.Select(c => KeyValuePair.Create(c.Key, (object)c.Value))
                        .Concat(_histograms.Select(h => KeyValuePair.Create(h.Key, (object)new { h.Value.Count, h.Value.Sum, h.Value.Max })))
                        .ToDictionary());
                }

                logger.LogInformation("Metrics since start {Metrics}", snapshot);
            }
        }
    }

    private static string Key(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        tags.IsEmpty ? instrument.Name : $"{instrument.Name}{{{string.Join(",", tags.ToArray().OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"))}}}";
}
