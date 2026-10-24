using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace IHub.Statements.IntegrationTests;

public class ActivityEndpointTests
{
    [SetUp]
    public Task SetUp() => ApiFixture.TruncateAsync();

    [Test]
    public async Task PostActivity_WhenNew_Returns201AndStoresOneRow()
    {
        var response = await PostActivityAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await CountRowsAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PostActivity_WhenRetriedIdentically_Returns200AndStoresOneRow()
    {
        await PostActivityAsync();

        var response = await PostActivityAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await CountRowsAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PostActivity_WhenRetriedWithSevenFractionalDigits_Returns200AndStoresOneRow()
    {
        await PostActivityAsync("occurredAt", "2026-10-01T10:00:00.1234567Z");

        var response = await PostActivityAsync("occurredAt", "2026-10-01T10:00:00.1234567Z");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await CountRowsAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PostActivity_WhenSameKeyHasDifferentAmount_Returns409AndKeepsFirstRow()
    {
        await PostActivityAsync();

        var response = await PostActivityAsync("amount", 999m);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Status, Is.EqualTo(StatusCodes.Status409Conflict));
        Assert.That(problem.Title, Is.EqualTo("An activity with this source and id already exists with different content."));
        await using var connection = new NpgsqlConnection(ApiFixture.ConnectionString);
        var amount = await connection.ExecuteScalarAsync<decimal>(
            "SELECT amount FROM intermediary_activity WHERE source = @source AND activity_id = @activityId",
            new { source = "crm", activityId = "a1" });
        Assert.That(amount, Is.EqualTo(100.50m));
    }

    private static IEnumerable<TestCaseData> InvalidFields() =>
    [
        new("activityId", " "),
        new("amount", 0m),
        new("amount", 1.23456m),
        new("amount", 1_000_000_000_000_000m),
        new("source", new string('a', 65)),
        new("currency", "USD"),
        new("occurredAt", DateTimeOffset.UtcNow.AddMinutes(10)),
        new("occurredAt", null)
    ];

    [TestCaseSource(nameof(InvalidFields))]
    public async Task PostActivity_WhenFieldIsInvalid_Returns400NamingTheFieldAndStoresNothing(string field, object? value)
    {
        var response = await PostActivityAsync(field, value);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.That(problem!.Errors.Keys, Is.EquivalentTo(new[] { field }));
        Assert.That(await CountRowsAsync(), Is.Zero);
    }

    [TestCase("occurredAt", "2026-10-01T10:00:00")]
    public async Task PostActivity_WhenFieldIsUnreadable_Returns400AndStoresNothing(string field, string value)
    {
        var response = await PostActivityAsync(field, value);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(await CountRowsAsync(), Is.Zero);
    }

    [Test]
    public async Task PostActivity_WhenTwentyIdenticalPostsRace_StoresOneRowAndEveryResponseIs201Or200()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostActivityAsync()));

        Assert.That(responses.Select(response => response.StatusCode), Is.All.AnyOf(HttpStatusCode.Created, HttpStatusCode.OK));
        Assert.That(await CountRowsAsync(), Is.EqualTo(1));
    }

    private static Task<HttpResponseMessage> PostActivityAsync(string? field = null, object? value = null)
    {
        Dictionary<string, object?> body = new()
        {
            ["source"] = "crm",
            ["activityId"] = "a1",
            ["amount"] = 100.50m,
            ["currency"] = "ZAR",
            ["occurredAt"] = DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture)
        };
        if (field is not null)
        {
            body[field] = value;
        }

        return ApiFixture.Client.PostAsJsonAsync("/intermediaries/int-1/activities", body);
    }

    private static async Task<long> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.ConnectionString);
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM intermediary_activity");
    }
}
