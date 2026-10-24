# Solution

The service stores each activity as one immutable row in a single PostgreSQL table. The primary key
stops duplicates, and a statement is a sum calculated when it is read. No row is shared between
submissions, and a sum has no order to get wrong.

## Task 1: Activity ingestion and statement retrieval

### API and data model

`POST /intermediaries/{intermediaryId}/activities` takes `source`, `activityId`, `amount`,
`currency` and `occurredAt`. It answers 201 for a new activity, 200 for an identical resend, 409 for
a resend with different content and 400 for anything invalid, with problem details for errors.
`GET /intermediaries/{intermediaryId}/statement?from=&to=` returns the window in UTC, `grossTotal`,
`currency` and `lastUpdatedAt`, which is null when the window has no rows.

The table `intermediary_activity` has the primary key `(source, activity_id)`, because an id is only
unique within the system that sent it. The CHECKs refuse blank ids, a zero amount and anything but
ZAR, and the database stamps `received_at`. An index on `(intermediary_id, occurred_at)` serves the
statement. The API checks the same rules first; the CHECKs are the backstop.

```text
  client
    |  POST /intermediaries/{id}/activities    -> 201 | 200 | 409 | 400
    |  GET  /intermediaries/{id}/statement     -> grossTotal, lastUpdatedAt
    v
  +------------------------------------------------------+
  | ASP.NET Core minimal API, one container              |
  | validate, then Dapper over an Npgsql data source     |
  | /health/live    /health/ready (SELECT 1)             |
  +------------------------------------------------------+
                              |  INSERT ... ON CONFLICT (source, activity_id) DO NOTHING
                              |  SELECT sum(amount), max(received_at) over [from, to)
                              v
  +------------------------------------------------------+
  | PostgreSQL 18: table intermediary_activity           |
  +------------------------------------------------------+
```

```text
intermediary_activity
  source           text           PK (with activity_id)   CHECK 1-64 chars, trimmed
  activity_id      text           PK (with source)        CHECK 1-64 chars, trimmed
  intermediary_id  text                                   CHECK 1-64 chars, trimmed
  amount           numeric(19,4)                          CHECK <> 0
  currency         char(3)                                CHECK = 'ZAR'
  occurred_at      timestamptz
  received_at      timestamptz                            DEFAULT clock_timestamp()
  INDEX (intermediary_id, occurred_at)
```

### Assumptions

An activity is a money fact and every row counts toward the gross total. A stored fact never
changes: a reversal is a new activity with a negative amount. There is one currency, ZAR;
multi-currency means dropping the CHECK and grouping the sum by currency. Intermediary ids are
opaque, so an unknown one gets a zero total. `occurredAt` decides the window; past values are
accepted, more than five minutes ahead is refused.

### Correct totals

The insert is `INSERT ... ON CONFLICT (source, activity_id) DO NOTHING`, so of two racing identical
posts the second waits on the unique index and inserts nothing. When no row goes in, the handler
compares the stored intermediary, amount, currency and occurred-at with the request: a match is a
retry and gets 200, a difference gets 409 and the stored fact stands.

A late activity lands in the window of its own `occurredAt`, so a past window is restated on the
next read. Concurrent submissions for one intermediary share no row, so the only race is two inserts
of one key, which the primary key settles. The window is half-open, `[from, to)`, so a boundary
activity counts once. Bounds and `occurredAt` need an explicit offset, because .NET reads a
timestamp without one as local time and a broker's midnight read as UTC moves the window two hours
silently. `lastUpdatedAt` is `MAX(received_at)` over the window, so it moves when a late fact lands.
It is the latest arrival, not the moment the total last changed: two inserts that overlap can commit
in the other order.

Ingestion is synchronous: a 2xx means committed and visible. The tests drive the real app host over
HTTP against the real PostgreSQL from compose; nothing is mocked, because the guarantee lives in the
primary key.

### Growth

A new source works today, because the source is in the key. A new activity type is a `type` column
with a filtered sum. If a source must amend rather than reverse, a revision column with
`DO UPDATE ... WHERE revision < excluded.revision` is the upgrade.

## Task 2: Lightweight observability and operational signals

### Signals

Logs are JSON with scopes on, so each request's W3C trace id appears on its starting and finished
lines and in every problem details body. The meter `IHub.Statements` has `ihub.activities.ingested`,
counting posts by outcome (created, duplicate, conflict, invalid), and
`ihub.activity.source_lateness`, seconds from `occurredAt` to storage: the brief's processing delay
in a synchronous service. Speed and error rate come from the built-in `http.server.request.duration`
with its status codes. `/health/live` has no checks; `/health/ready` runs `SELECT 1` and answers 503
when the database is unreachable. Locally a background service logs the totals every 30 seconds
(count, sum and max); in AWS the same meter would go through the OpenTelemetry collector to
CloudWatch, which gives the p95 the alerts below need.

### Two alert ideas

Errors: 5xx above 1 percent of ingestion requests over 5 minutes. Check the ready check, the
database's connections, CPU and storage, and roll back a coinciding deploy.

Delay and silence: p95 source lateness above 2 hours for 15 minutes, or zero created for 30 minutes
in business hours. Check queue depth, the oldest message's age, the dead-letter queue and the
upstream feed.

## Task 3: AWS run plan

### Architecture overview

ECS on Fargate runs one image in two roles, the HTTP API behind an ALB and a queue consumer, with
SQS between them and RDS PostgreSQL behind. The container and PostgreSQL stay portable (the same
image would run on Kubernetes); the queue and the managed database are accepted lock-in. Lambda
with SQS is a fair alternative, but the service is busy enough to stay warm, one image serves
both roles, and Lambda's connection fan-out would need capping or RDS Proxy. Aurora's replicas,
faster failover and serverless scaling are not needed at this volume and cost more.

```text
  product systems
        |  POST activity
        v
  +--------------------------------+
  | ALB                            |
  +--------------------------------+
                  |
                  v
  +--------------------------------+
  | ECS Fargate: API tasks         |  validate, enqueue, answer 202
  | scale on CPU                   |
  +--------------------------------+
                  |  SendMessage
                  v
  +--------------------------------+      +---------------------------+
  | SQS queue                      |----> | dead-letter queue + alarm |
  +--------------------------------+      | (conflicting resends)     |
                                          +---------------------------+
                  |  receive, capped concurrency
                  v
  +--------------------------------+
  | ECS Fargate: consumer tasks    |  INSERT ... ON CONFLICT DO NOTHING
  | scale on queue depth (capped)  |
  +--------------------------------+
                  |
                  v
  +--------------------------------+
  | RDS PostgreSQL                 |
  +--------------------------------+

  One image in ECR runs both roles; Secrets Manager injects the connection string.
  GET statement: the API task reads RDS directly.
```

### Deployment

In production the API validates, enqueues to SQS and answers 202, so a malformed activity is
refused at the door and a burst lands on the queue, not the database. The consumer runs the same
insert with capped concurrency and deletes a message only after the commit, so a failure is
redelivered after the visibility timeout; redelivered duplicates are no-ops and a conflicting
resend goes to the dead-letter queue with an alarm. Locally the slice inserts directly, because
the brief allows one ingestion mechanism and the insert is the same code.

CI builds the image once (the tests stage is the gate), tags it with the commit SHA in ECR and
deploys to staging on merge and to production on approval. Migrations run first as a one-off ECS
task (`migrate`) and a failure stops the pipeline. Rollback is the previous task definition
revision, which cannot undo a schema change, and rollouts overlap old and new tasks, so migrations
are backward compatible: add and backfill first, drop in a later release.

The consumer's ready check is the database and the API's is the queue, so during a database outage
the API keeps accepting posts and messages wait, while statement reads fail until it returns.
Secrets Manager injects `ConnectionStrings__Statements`; nothing is in the image. At an assumed peak
of ten thousand activities an hour (under three inserts a second) one small RDS instance is enough;
consumers scale on queue depth up to a cap sized to what the database can take (connections and
inserts), so a backlog waits in the queue rather than overloading RDS; API tasks scale on CPU.

## Trade-offs and what is not built

The brief asks for a production-minded slice in about four hours. I put the production weight on
database-enforced correctness, the observability signals and the Task 3 plan, and left the rest as
not built.

Invalid input is a 400, not a 422, and field validation names the field in the body. The request
body is `IntermediaryActivity` so it does not clash with `System.Diagnostics.Activity` and
OpenTelemetry traces. The explicit-offset rule has no built-in switch, so a small JSON converter
enforces it on the body and the statement bounds are parsed as strings under the same rule. That may
be more machinery than two routes need, and the two paths report errors differently (a generic 400
for the body, a named field for a bound), but a silent two-hour shift in a money statement is the
trade-off I chose to pay for. A bound with eight or more fractional digits is rounded by .NET before
the microsecond check and can pass; I left that out for simplicity and the four-hour limit. Batch
ingestion, 422 with per-rule codes, a correlation header, multi-currency and the covering `INCLUDE`
index are not built; nothing at this volume needs them.

## How AI tools helped

I used an AI coding assistant for options and drafts: for each design choice it laid out the options
with their trade-offs and a recommendation, I chose, and it drafted code against the decision. It
found why every connection logged "Cannot load library libgssapi_krb5.so.2" and showed that the
framework's query binding accepts a timestamp without an offset, which is why I parse the bounds by
hand. I overruled it where I disagreed: it wanted the types named `Activity` and `Statement` and
missed the clash with `System.Diagnostics.Activity`, and I cut its observability plan to one
histogram. I checked its work by watching each test fail first and by breaking the production code
on purpose to see the important tests go red. It drafted the prose of README.md and SOLUTION.md from my decision log; I reviewed and approved it.
