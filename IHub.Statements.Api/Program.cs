using System.Text.Json;
using IHub.Statements.Api.Activities;
using IHub.Statements.Api.Database;
using IHub.Statements.Api.Observability;
using IHub.Statements.Api.Statements;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Statements")
    ?? throw new InvalidOperationException("ConnectionStrings:Statements is not configured.");

if (args is ["migrate"])
{
    await Schema.ApplyAsync(connectionString);
    return;
}

// The hosting scope carries the W3C TraceId and SpanId onto every line; the trace id is the correlation id.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    // Indented only in the opt-in Development run (README); Production stays one line per entry for grep and shippers.
    options.JsonWriterOptions = new JsonWriterOptions { Indented = builder.Environment.IsDevelopment() };
});

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IngestionMetrics>();
builder.Services.AddSingleton<ActivityEndpoint>();
builder.Services.AddSingleton<StatementEndpoint>();
builder.Services.AddHostedService<MetricsReporter>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddProblemDetails();
// Development would throw a bad request into the exception handler; this keeps the Production path, an empty 400
// that UseStatusCodePages turns into a ProblemDetails body, in every environment.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new OffsetRequiredDateTimeOffsetConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.MapPost("/intermediaries/{intermediaryId}/activities",
    (ActivityEndpoint endpoint, [AsParameters] IngestActivityRequest request, CancellationToken cancellationToken) =>
        endpoint.PostAsync(request, cancellationToken));
app.MapGet("/intermediaries/{intermediaryId}/statement",
    (StatementEndpoint endpoint, [AsParameters] StatementRequest request, CancellationToken cancellationToken) =>
        endpoint.GetAsync(request, cancellationToken));

app.Run();

// WebApplicationFactory<Program> in the test project needs this type to be public.
public partial class Program;
