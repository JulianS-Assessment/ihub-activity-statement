using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using IHub.Statements.Api.Activities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IHub.Statements.IntegrationTests;

public class ObservabilityTests
{
    private static readonly IntermediaryActivity Activity = new("crm", "a1", 100.50m, "ZAR", DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture));

    [SetUp]
    public Task SetUp() => ApiFixture.TruncateAsync();

    [Test]
    public async Task GetHealth_WhenDatabaseIsUp_LiveAndReadyReturn200()
    {
        var live = await ApiFixture.Client.GetAsync("/health/live");
        var ready = await ApiFixture.Client.GetAsync("/health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task GetHealth_WhenDatabaseIsUnreachable_LiveReturns200AndReadyReturns503()
    {
        await using var factory = UnreachableDatabase();
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task PostActivity_WhenNewThenRetried_CountsCreatedThenDuplicateAndRecordsSourceLateness()
    {
        var measurements = new ConcurrentQueue<(string Instrument, double Value, string? Outcome)>();
        using var listener = Listen(measurements);

        await ApiFixture.Client.PostAsJsonAsync("/intermediaries/int-1/activities", Activity);
        await ApiFixture.Client.PostAsJsonAsync("/intermediaries/int-1/activities", Activity);

        Assert.That(Outcomes(measurements), Is.EqualTo(new[] { "created", "duplicate" }));
        var lateness = measurements.Where(m => m.Instrument == "ihub.activity.source_lateness").Select(m => m.Value);
        Assert.That(lateness, Has.Exactly(1).GreaterThan(0));
    }

    [Test]
    public async Task PostActivity_WhenInvalid_CountsInvalidOnce()
    {
        var measurements = new ConcurrentQueue<(string Instrument, double Value, string? Outcome)>();
        using var listener = Listen(measurements);

        await ApiFixture.Client.PostAsJsonAsync("/intermediaries/int-1/activities", Activity with { Currency = "USD" });

        Assert.That(Outcomes(measurements), Is.EqualTo(new[] { "invalid" }));
    }

    private static WebApplicationFactory<Program> UnreachableDatabase() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Statements", "Host=nowhere.invalid;Username=x;Password=x;Database=x;Timeout=2"));

    // The meter name is the contract a dashboard would use, so it is spelled out rather than read from the API.
    private static MeterListener Listen(ConcurrentQueue<(string Instrument, double Value, string? Outcome)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "IHub.Statements")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, OutcomeOf(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, OutcomeOf(tags))));
        listener.Start();
        return listener;
    }

    private static string? OutcomeOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "outcome")
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private static string?[] Outcomes(IEnumerable<(string Instrument, double Value, string? Outcome)> measurements) =>
        measurements.Where(m => m.Instrument == "ihub.activities.ingested").Select(m => m.Outcome).ToArray();
}
