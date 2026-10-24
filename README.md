# Activity statements

A small .NET service that takes intermediary activities over HTTP and stores them in PostgreSQL,
then returns a statement: the gross total for one intermediary over a time window. It stays correct
when an activity arrives twice, arrives late, or races another submission for the same intermediary.

Video: [walkthrough and demo](https://1drv.ms/v/c/20182de4ad3e5c88/IQAw5vOnVxWrSq7q-_KTP0X-AWbAUCq9queMYL7S3qLYsd8?e=EEnFTY).
The design, the trade-offs and the architecture and AWS diagrams are in [SOLUTION.md](SOLUTION.md).

## Running it

All you need is a Docker runtime with compose. Every build, test and run happens in containers, so
no .NET SDK is needed on the host. Port 8080 must be free, and `jq` helps for reading the statements
and the logs.

```
docker compose run --rm api migrate
docker compose up
```

The first command starts PostgreSQL, waits for it to be healthy and applies `schema.sql`, which is
idempotent. The second starts the database and the API on http://localhost:8080 and keeps the
terminal, so use another one for everything below. After changing code, add `--build` to the run
commands. `docker compose down` stops everything and keeps the data volume.

To read the logs more easily, start the API in Development instead of `docker compose up`. It
indents each JSON entry over several lines; Production, the default, keeps one line per entry so
`grep` and `jq` work. Stop `docker compose up` first, because both use port 8080:

```
docker compose run --rm --service-ports -e ASPNETCORE_ENVIRONMENT=Development api
```

## Trying it

Post an activity:

```
curl -i localhost:8080/intermediaries/broker-42/activities -H 'Content-Type: application/json' \
  -d '{"source":"policy-admin","activityId":"a-1001","amount":1250.50,"currency":"ZAR","occurredAt":"2026-10-01T09:15:00+02:00"}'
```

The first post returns 201. Run it again and it returns 200 and stores nothing new. A missing field,
a zero amount, a currency other than ZAR or a timestamp without an offset is a 400. Post the same
`source` and `activityId` with a different amount and you get a 409 with a problem details body,
and the stored row stays as it was:

```
curl -i localhost:8080/intermediaries/broker-42/activities -H 'Content-Type: application/json' \
  -d '{"source":"policy-admin","activityId":"a-1001","amount":1300.00,"currency":"ZAR","occurredAt":"2026-10-01T09:15:00+02:00"}'
```

Read the statement for October:

```
curl -s 'localhost:8080/intermediaries/broker-42/statement?from=2026-10-01T00:00:00%2B02:00&to=2026-11-01T00:00:00Z' | jq
```

The statement echoes the window in UTC with `grossTotal`, `currency` and `lastUpdatedAt`. Both bounds
need an explicit offset: use `Z`, or send `+` as `%2B`, because a bare `+` in a query string decodes
to a space.

A late activity from another source lands in the window of its own `occurredAt`, so the next
statement is restated and `lastUpdatedAt` moves. Post this reversal:

```
curl -i localhost:8080/intermediaries/broker-42/activities -H 'Content-Type: application/json' \
  -d '{"source":"claims","activityId":"r-77","amount":-250.50,"currency":"ZAR","occurredAt":"2026-10-03T14:00:00+02:00"}'
```

Then read the statement again:

```
curl -s 'localhost:8080/intermediaries/broker-42/statement?from=2026-10-01T00:00:00%2B02:00&to=2026-11-01T00:00:00Z' | jq
```

Check that it is up. Live says the process is serving; ready also checks the database and answers
503 when it cannot reach it:

```
curl -i localhost:8080/health/live
curl -i localhost:8080/health/ready
```

## Reading the logs

The API writes JSON to the console. Every request writes a starting line and a finished line, and
both carry the request's `TraceId`, so `docker compose logs --no-log-prefix api | jq` lets you
follow one request. Those `docker compose logs` commands read the `docker compose up` container; the
Development run prints the same lines in its own terminal. Each posted activity also writes an
outcome line (created, duplicate, conflict or invalid) with the same trace id, which this filter
lists:

```
docker compose logs --no-log-prefix api | jq -c 'select(.Scopes[0].TraceId) | {Message, TraceId: .Scopes[0].TraceId}'
```

Every 30 seconds a line with the metric totals since start goes out as well:

```
docker compose logs --no-log-prefix api | jq 'select(.State.Metrics) | .State.Metrics | fromjson'
```

To see it sooner, start the API this way instead of `docker compose up`, and the line appears in
that terminal every five seconds:

```
docker compose run --rm --service-ports -e Metrics__ReportInterval=00:00:05 api
```

## Running the tests

Run these last: the tests truncate the activity table, so they wipe anything you posted by hand. They
run against the same database and read their connection string from the environment `compose.yaml`
sets for the tests service, so they are meant to run through this command, in another terminal:

```
docker compose run --rm tests
```

The database password in `compose.yaml` is a throwaway for the local container, kept there for
simplicity and never for production. The stopgap there would be a gitignored `.env` with a committed
`.env.example`; the plan is in SOLUTION.md, Task 3.
