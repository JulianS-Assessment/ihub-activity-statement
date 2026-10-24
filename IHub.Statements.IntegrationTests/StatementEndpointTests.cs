using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using IHub.Statements.Api.Activities;
using IHub.Statements.Api.Statements;
using Microsoft.AspNetCore.Http;

namespace IHub.Statements.IntegrationTests;

public class StatementEndpointTests
{
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);

    [SetUp]
    public Task SetUp() => ApiFixture.TruncateAsync();

    [Test]
    public async Task GetStatement_WhenWindowIsEmpty_ReturnsZeroTotalAndNullLastUpdated()
    {
        var statement = await GetStatementAsync();

        Assert.That(statement, Is.EqualTo(new StatementResponse("int-1", From, To, 0m, "ZAR", null)));
    }

    [Test]
    public async Task GetStatement_WhenActivitiesFallInsideAndOutsideWindow_SumsOnlyInside()
    {
        await PostActivityAsync("before", 1000m, "2026-08-31T23:59:59Z");
        await PostActivityAsync("at-from", 100m, "2026-09-01T02:00:00+02:00");
        await PostActivityAsync("inside", 20m, "2026-09-15T12:00:00Z");
        await PostActivityAsync("at-to", 3000m, "2026-10-01T00:00:00Z");
        await PostActivityAsync("after", 50000m, "2026-10-01T00:00:01Z");

        var statement = await GetStatementAsync();

        Assert.That(statement.GrossTotal, Is.EqualTo(120m));
        Assert.That(statement.LastUpdatedAt, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task GetStatement_WhenAnActivityIsNegative_ReducesTotal()
    {
        await PostActivityAsync("sale", 100m, "2026-09-10T00:00:00Z");
        await PostActivityAsync("reversal", -30.25m, "2026-09-11T00:00:00Z");

        var statement = await GetStatementAsync();

        Assert.That(statement.GrossTotal, Is.EqualTo(69.75m));
    }

    [Test]
    public async Task GetStatement_WhenActivityBelongsToAnotherIntermediary_ExcludesIt()
    {
        await PostActivityAsync("mine", 100m, "2026-09-10T00:00:00Z");
        await PostActivityAsync("theirs", 50m, "2026-09-10T00:00:00Z", intermediaryId: "int-2");

        var statement = await GetStatementAsync();

        Assert.That(statement.GrossTotal, Is.EqualTo(100m));
    }

    [Test]
    public async Task GetStatement_WhenLateActivityArrives_RestatesTheWindowAndAdvancesLastUpdated()
    {
        await PostActivityAsync("on-time", 100m, "2026-09-15T00:00:00Z");
        var first = await GetStatementAsync();

        await PostActivityAsync("late", 50m, "2026-09-02T00:00:00Z");
        var second = await GetStatementAsync();

        Assert.That(first.GrossTotal, Is.EqualTo(100m));
        Assert.That(second.GrossTotal, Is.EqualTo(150m));
        Assert.That(second.LastUpdatedAt, Is.GreaterThan(first.LastUpdatedAt!.Value));
        Assert.That(second.LastUpdatedAt, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task GetStatement_WhenFromIsNotBeforeTo_Returns400()
    {
        var response = await SendStatementRequestAsync(from: "2026-09-01T00:00:00Z", to: "2026-09-01T00:00:00Z");

        await AssertValidationProblemAsync(response, "from");
    }

    [TestCase(null, "2026-10-01T00:00:00Z", "from")]
    [TestCase("2026-09-01T00:00:00Z", null, "to")]
    [TestCase("2026-09-01T00:00:00", "2026-10-01T00:00:00Z", "from")]
    [TestCase("2026-09-01T00:00:00Z", "2026-10-01T00:00:00", "to")]
    [TestCase("2026-09-01T00:00:00Z", "2026-10-01T00:00:00.1234567Z", "to")]
    public async Task GetStatement_WhenBoundIsMissingOrUnreadable_Returns400(string? from, string? to, string field)
    {
        var response = await SendStatementRequestAsync(from, to);

        await AssertValidationProblemAsync(response, field);
    }

    private static async Task PostActivityAsync(string activityId, decimal amount, string occurredAt, string intermediaryId = "int-1")
    {
        var response = await ApiFixture.Client.PostAsJsonAsync(
            $"/intermediaries/{intermediaryId}/activities",
            new IntermediaryActivity("crm", activityId, amount, "ZAR", DateTimeOffset.Parse(occurredAt, CultureInfo.InvariantCulture)));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    private static async Task<StatementResponse> GetStatementAsync()
    {
        var response = await SendStatementRequestAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<StatementResponse>())!;
    }

    private static Task<HttpResponseMessage> SendStatementRequestAsync(string? from = "2026-09-01T00:00:00Z", string? to = "2026-10-01T00:00:00Z")
    {
        string[] bounds =
        [
            .. from is null ? [] : new[] { $"from={Uri.EscapeDataString(from)}" },
            .. to is null ? [] : new[] { $"to={Uri.EscapeDataString(to)}" }
        ];
        return ApiFixture.Client.GetAsync($"/intermediaries/int-1/statement?{string.Join('&', bounds)}");
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.That(problem!.Errors.Keys, Is.EquivalentTo(new[] { field }));
    }
}
