# US-03 — Create Job

**Jira:** _not recorded — fill in_
**Repos touched:** `assms-job-service`, `assms-frontend`, `assms-platform-infrastructure`
**Service:** Job Service · **Database:** `jobdb` · **Table:** `jobs`
**Depends on:** US-02A — Create Asset *(a job is raised against an asset)* · `assms-customer-asset-service/docs/asset-management/US-02A-create-asset.md`
**Frontend note:** US-03 — Create Job (frontend) · `assms-frontend/docs/api-integration/US-03-create-job.md`
**Event contract:** ASSMS Kafka Event Contracts · `assms-platform-infrastructure/docs/kafka/event-contracts.md`

> As an Agent, I want to raise a service job against a customer's unit, so that the work
> is recorded with a reference I can quote and Dispatch can pick it up.

---

## 1. What was built

A job creation flow, end to end — and the first ASSMS service that talks to another
service over HTTP and publishes to Kafka:

```
React form  →  POST /api/jobs  →  JobService  →  JobRepository  →  jobdb
                                      │
                                      ├─→ IAssetValidationClient ──→ GET /api/assets/{id}      ┐ customer
                                      │                          └─→ GET /api/customers/{id}   ┘ service
                                      │
                                      └─→ IEventPublisher ──────────→ Kafka topic `job-created`
```

| Layer | Files |
|---|---|
| Database | `database/migrations/V01__create_jobs.sql` |
| Migration runner | `scripts/development/apply_migrations.ps1` (copied unchanged from the customer service) |
| Model | `Models/Job.cs` |
| DTOs | `DTOs/CreateJobRequest.cs`, `DTOs/JobResponse.cs` |
| Helper | `Extensions/JobReferenceGenerator.cs` |
| Data access | `Repositories/IJobRepository.cs`, `Repositories/JobRepository.cs` |
| Cross-service | `Services/IAssetValidationClient.cs`, `Services/AssetValidationClient.cs` |
| Messaging | `Messaging/Contracts/EventEnvelope.cs`, `Messaging/Contracts/JobCreatedPayload.cs`, `Messaging/Producers/IEventPublisher.cs`, `Messaging/Producers/KafkaEventPublisher.cs` |
| Business logic | `Services/JobService.cs`, `Services/Result.cs` |
| HTTP | `Controllers/JobsController.cs` |
| Wiring | `Program.cs`, `appsettings.Development.json`, `appsettings.Example.json` |
| Tests | `tests/JobService.Tests/UnitTests/*`, `tests/JobService.Tests/Fakes/*` |

`IDbConnectionFactory` and `MySqlConnectionFactory` already existed from the earlier
wiring task and were reused unchanged.

---

## 2. The `jobs` table

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) | PK. UUID generated in C#, not by MySQL |
| `job_reference` | VARCHAR(20) | UNIQUE. `JOB-` + six characters — see section 4 |
| `customer_id` | CHAR(36) | **No FK** — different database, see below |
| `asset_id` | CHAR(36) | **No FK** — same reason |
| `service_category` | VARCHAR(20) | CHECK: `INSTALLATION` \| `REPAIR` \| `MAINTENANCE` \| `INSPECTION` \| `WARRANTY_CLAIM` |
| `problem_description` | VARCHAR(1000) | required |
| `priority` | VARCHAR(10) | CHECK: `LOW` \| `MEDIUM` \| `HIGH` \| `URGENT` |
| `region` | VARCHAR(20) | CHECK: the nine Sri Lankan provinces |
| `scheduled_date` | DATE | nullable — **always NULL today**, see section 7 |
| `created_by` | CHAR(36) | required — **always the all-zero GUID today**, see section 7 |
| `status` | VARCHAR(20) | DEFAULT `'CREATED'` |
| `created_at` | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP |
| `updated_at` | TIMESTAMP | DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP |

Indexes: PK on `id`, UNIQUE on `job_reference`, plain indexes on `customer_id`,
`asset_id` and `region`.

Declared inline in the `CREATE TABLE` body, like the customer service's migrations, so
the whole thing stays safe to re-run — MySQL 8 has no `CREATE INDEX IF NOT EXISTS`.

`idx_jobs_region` is not speculative. Dispatch filters by region to find the technicians
who cover it, so that index is what its Sprint 2 assignment queries run on.

Three CHECK constraints, no fourth on `status`: the lifecycle grows in later sprints, and
a CHECK there would have to be altered by every story that adds a state.

### Why there are no foreign keys

`customer_id` and `asset_id` name rows in `customerdb`. That is a **different database,
owned by a different service, reached with different credentials** — `job_svc` has no
grant on `customerdb` at all. MySQL cannot constrain across that boundary, and even if
the two schemas shared a server, a foreign key would couple the services' schemas and
make the Job Service unable to deploy independently.

The reference is validated over HTTP before the insert instead. That is section 3, and
it is the whole reason this story is a precedent rather than routine CRUD.

---

## 3. Cross-service validation — the precedent

**This is the first time an ASSMS service calls another over HTTP.** How it is done here
is how the next one should be done, so the reasoning matters more than the code.

### Why it cannot be a database join

The database isolation is deliberate and was verified, not assumed:

- `customerdb` and `jobdb` are separate databases created by separate init scripts.
- `job_svc` is granted `ALL PRIVILEGES ON jobdb.*` and nothing else. It cannot read
  `customers` or `assets` even if a query tried.
- In staging they are separate schemas behind a private MySQL Flexible Server, and the
  services run on separate VMs.

So `JOIN customers ON …` is not a design choice that was rejected — it is not available.
The only party that can answer "is this a real, active asset belonging to this active
customer?" is the service that owns the data.

### One call answers four questions

`GET /api/assets/{id}` on the customer service returns the asset's `customerId` and its
`status`. From one response the client settles:

| Question | How |
|---|---|
| Does the asset exist? | 404 → `AssetNotFound` |
| Does it belong to this customer? | compare `customerId` → `CustomerMismatch` |
| Is the asset usable? | `status != ACTIVE` → `AssetInactive` |

Ownership is checked **before** status, because raising a job against someone else's
equipment is the wrong asset entirely — whether that asset happens to be active is beside
the point.

A **second** call, `GET /api/customers/{id}`, settles the fourth question. It is needed
because the asset representation carries the owner's id but not the owner's status, and
an active asset can belong to a customer who has since been deactivated. If that call
404s the outcome is reported as `CustomerMismatch`: customers are deactivated rather than
deleted, so it is a data anomaly rather than a reachable state, and "the pairing could not
be confirmed" is the closest true statement.

### Base URL from configuration

`CustomerService:BaseUrl`, read in `Program.cs` and thrown on if absent — no fallback
that would quietly send requests somewhere unexpected. Registered with `AddHttpClient`,
which pools and recycles handlers; a `new HttpClient()` per call is what exhausts sockets
and pins stale DNS in a long-running process.

The base address is normalised with `TrimEnd('/') + "/"`. Without the trailing slash a
relative path replaces the last segment of the base rather than extending it, which
breaks the moment anyone configures a URL with a path prefix.

### The distinction that matters: 409 versus 503

This is the part worth carrying to every future cross-service call.

| | The asset is invalid | The service is unreachable |
|---|---|---|
| What happened | The customer service **answered**, and the answer was no | The customer service **did not answer** |
| Examples | 404, wrong owner, inactive asset, inactive customer | connection refused, timeout, 500, unreadable body |
| Whose problem | The Agent's — they picked the wrong thing | Ours — infrastructure |
| Can the Agent fix it? | Yes, by changing a field | No |
| Response | **409** keyed on the field to correct | **503**, safe to retry unchanged |
| Carried as | `AssetValidationOutcome` value | `AssetValidationUnavailableException` |

**This is why `AssetValidationUnavailableException` exists rather than a sixth outcome
value.** An outcome is a verdict on the request. "Unreachable" is not a verdict — it is
the absence of one. Folding it into the enum would make every `switch` over outcomes
treat a broker-level failure as a business refusal, and the natural mapping would then
reject a job that might be perfectly valid. Rejecting valid work because a dependency
blipped is a worse failure than asking the Agent to try again.

The type system enforces it: `ValidateAsync` returns `AssetValidationOutcome`, so there
is no representation of "unavailable" for a caller to accidentally handle as a refusal.
`JobService.CreateAsync` deliberately does not catch it; `JobsController` does, and turns
it into the 503.

Note that a non-404 error status — a 500 from the customer service — is an *infrastructure*
failure, not a validation one. Only 404 is an answer.

---

## 4. The job reference

`JOB-` plus six characters from a 32-character alphabet. `JOB-7K2M9X`.

### Random, not sequential

A sequential reference needs a counter, and a counter needs either a database sequence or
a read-modify-write under a lock. Both are contention points, both serialise job creation,
and the locking one has a race the moment two Agents create a job at the same instant.

Random has none of that: **no counter, no locking, no race.** It also does not leak how
many jobs the business has taken, which a customer can otherwise read straight off their
own reference.

`RandomNumberGenerator`, not `Random`. The value is a customer-facing identifier, and a
predictable one lets a caller guess other people's references.

### The reduced alphabet

```
0123456789ABCDEFGHJKMNPQRSTVWXYZ        32 characters
```

Crockford's base32: the ten digits and twenty-six letters, less **I, L, O and U**.
36 − 4 = 32.

The reason is that **Agents read these aloud.** I and L come back as 1, O comes back as
0, and U is dropped so that six random characters cannot spell something the Agent would
rather not read down the phone. Six characters from 32 is about a billion references.

32 is also exactly a power of two, so a uniform draw over the alphabet has no modulo bias.

### The retry-on-1062 loop, and why this 1062 is different

There is no pre-check for an existing reference. Two requests could both pass one before
either inserted, so the **unique index is the only thing that actually settles it**. The
service generates a reference, inserts, and on a duplicate-key error draws a *fresh*
reference and tries again — up to three attempts.

Re-sending the reference that just collided would collide forever, which is why a fresh
one is drawn inside the loop rather than once before it.

**Every other 1062 catch in the codebase reports the duplicate to the user. This one hides
it.** The difference is whose fault it is:

| | `AssetService` / `CustomerService` | `JobService` |
|---|---|---|
| The duplicated value | A serial number or phone the **Agent typed** | A reference the **system drew** |
| What it means | Two records for one real thing | Bad luck |
| Can the Agent do anything? | Yes — type a different value | No — they never saw it |
| Response | 409, keyed on the field | Retry silently, log a warning |

Surfacing this one would be asking the Agent to fix a collision in a value they have no
control over and have never been shown.

**The last attempt is not caught.** If three independently drawn references all collide,
the odds say the generator or the alphabet is broken, not that the business has a billion
jobs. Swallowing that into a business error would hide a real defect, so it surfaces as a
500.

---

## 5. API

| Endpoint | Success | Failures |
|---|---|---|
| `POST /api/jobs` | **201** + the stored job, `Location` → `GET /api/jobs/{id}` | **400** field validation · **409** invalid asset/customer pairing · **503** validation unavailable |
| `GET /api/jobs/{id}` | **200** + the job | **404** |
| `GET /api/jobs/reference/{jobReference}` | **200** + the job | **404** |

The reference lookup is a **separate route**, not the id route accepting either form. A
mistyped id must read as a missing job, not get silently retried as a reference.

The four 409s are all `ValidationProblemDetails` keyed on the field to correct:

| `ServiceError` | Key | Message |
|---|---|---|
| `AssetNotFound` | `assetId` | No asset exists with this id. |
| `CustomerMismatch` | `assetId` | This asset is registered to a different customer. |
| `AssetInactive` | `assetId` | This asset has been deactivated and cannot have new jobs raised against it. |
| `CustomerInactive` | `customerId` | This customer has been deactivated and cannot have new jobs raised. |

`CustomerMismatch` is keyed on `assetId`, not `customerId`: the Agent picks the customer
first and then their equipment, so the asset is the field that is wrong.

Keying all four the same way is what lets the frontend render every refusal against its
input with one branch — the same convention as
§4 of `assms-customer-asset-service/docs/asset-management/US-02A-create-asset.md`.

`Program.cs` sets `DictionaryKeyPolicy = JsonNamingPolicy.CamelCase` so DataAnnotations
returns `assetId` rather than `AssetId`, matching the hand-built problems above.

CORS mirrors the customer service: named `Frontend` policy, origins from
`Cors:AllowedOrigins`, `UseCors` between `UseHttpsRedirection` and `UseAuthorization`.

---

## 6. Publish-after-persist, and the gap

### The ordering

```
1. Validate  →  409 / 503 and stop
2. Generate reference, build the job
3. Insert (retry on 1062)          ← the job now exists
4. Read the row back               ← for the database defaults
5. Publish JobCreated              ← try/catch: log and swallow
6. Return 201
```

Publish is **after** the commit, deliberately. Publishing first would announce a job that
might then fail to insert, and a consumer cannot un-see an event.

The read-back at step 4 is not ceremony: `status`, `created_at` and `updated_at` are
database defaults, so without it the response and the event would carry
`default(DateTime)` and the event's `createdAt` would be meaningless.

### Why the publish failure is swallowed

By step 5 the job is committed. The Agent is waiting on a request whose work is done.
Failing it now would tell them no job was raised while the row sits in the database — they
would create it again, and now there are two.

So the publish is wrapped in a `try/catch` that logs an error and returns success. The
job is the source of truth; the event is a notification about it.

### The honest part: this is the dual-write problem

Two systems are written in sequence with no transaction spanning them. **When step 5
fails, the event is genuinely lost.** Not delayed, not retried later — lost. Dispatch and
Reporting never learn the job exists, and nothing reconciles them afterwards.

The log line is the only trace, and nothing consumes the log.

#### The evidence

Four jobs in `jobdb`:

```
job_reference  service_category  priority  region         status   created_at
JOB-GCX43D     REPAIR            HIGH      WESTERN        CREATED  2026-08-29 04:52:50
JOB-JM9TP4     REPAIR            HIGH      WESTERN        CREATED  2026-08-29 04:53:35
JOB-N5TVMD     WARRANTY_CLAIM    LOW       NORTH_WESTERN  CREATED  2026-08-29 05:10:41
JOB-GV6ZD6     WARRANTY_CLAIM    LOW       NORTH_WESTERN  CREATED  2026-08-29 05:11:25
```

Two events on `job-created`, read from the beginning of the topic:

```
$ docker exec assms-kafka /opt/kafka/bin/kafka-console-consumer.sh \
    --bootstrap-server localhost:29092 --topic job-created \
    --from-beginning --timeout-ms 15000 --property print.key=true

9149e807-… | {"eventId":"338aab19-…","eventType":"JobCreated","eventVersion":1,
              "occurredAt":"2026-08-29T04:52:50.4240717Z","producer":"job-service",
              "payload":{…,"jobReference":"JOB-GCX43D",…,"status":"CREATED"}}
87dcfd3d-… | {"eventId":"11003713-…","eventType":"JobCreated","eventVersion":1,
              "occurredAt":"2026-08-29T04:53:35.3909159Z","producer":"job-service",
              "payload":{…,"jobReference":"JOB-JM9TP4",…,"status":"CREATED"}}

Processed a total of 2 messages
```

**`JOB-N5TVMD` and `JOB-GV6ZD6` exist in `jobdb` with no corresponding event.** Four rows,
two events. The topic has been read from offset zero, so they are not merely unread — they
were never published.

Take `JOB-GV6ZD6`: a real job, sitting in the database, with a reference an Agent could
have read to a customer. Dispatch will never assign it. Reporting will never count it. As
far as every other service is concerned it does not exist.

That is the whole failure mode in one row, and it is why the swallow in step 5 is a
trade-off rather than a fix.

#### The real fix: an outbox

Insert the event into an `outbox` table **in the same transaction as the job**, and have a
separate relay publish rows from it and mark them sent. One transaction, one database, so
either both the job and the pending event commit or neither does. The relay retries
independently and can be restarted; nothing is lost because the broker was down.

That turns the guarantee from at-most-once into at-least-once, which is why the event
contract already requires consumers to deduplicate on `eventId`.

Not built here. It is a story of its own, and it should be written before Dispatch starts
depending on `job-created` in Sprint 2.

---

## 7. Two settings that will look like mistakes

### `MessageTimeoutMs = 10000`

```csharp
var config = new ProducerConfig
{
    BootstrapServers = bootstrapServers,
    MessageTimeoutMs = 10000
};
```

`MessageTimeoutMs` is Confluent.Kafka's binding for librdkafka's `message.timeout.ms` —
the **entire budget for a message, internal retries included**, before `ProduceAsync`
faults.

**The default is 300000 — five minutes.** That default is written for a background
producer that can afford to keep retrying. Here the publish sits inside an HTTP request an
Agent is waiting on, and step 5 above is *awaited* before the 201 is returned. With the
default, a broker that is down would hang the Agent's request for five minutes before the
job they already created came back.

Ten seconds is long enough to ride out a leader election or a brief network stall, and
short enough that a person will still be waiting when the answer arrives. The failure path
is already correct — the job is committed and the 201 is returned regardless — so the only
thing this value controls is **how long the Agent waits before being told it worked**.

Do not delete this as a magic number. Deleting it restores the five-minute default.

> **Discrepancy to settle:** the brief that requested this documentation said "why 5000
> and not the default". The code says `10000`, which is what was actually asked for at the
> time it was set ("set message.timeout.ms to 10 seconds"). This section documents the code
> as it stands. If 5 seconds was the intent, change the constant and this section together
> — they must not drift.

### The placeholder columns

Both will read as bugs to whoever opens the table next. Neither is.

| Column | Value today | Why | Cleared by |
|---|---|---|---|
| `created_by` | `00000000-0000-0000-0000-000000000000` on every row | There is no authentication yet, so there is no Agent id to record | The auth story |
| `scheduled_date` | `NULL` on every row | Nothing schedules work yet; a job is raised before it is booked | The scheduling story |

Verified across the whole table — 4 of 4 rows on both:

```
total  zero_guid  null_schedule
4      4          4
```

**Why the all-zero GUID and not an empty string.** `created_by` is `CHAR(36)`, and
MySqlConnector reads `CHAR(36)` as a `Guid` by default (`GuidFormat=Char36`). An empty
string is not a parseable GUID, so writing `''` would make **every subsequent read of the
row throw** — `GetByIdAsync`, `GetByReferenceAsync` and the read-back on the create path
alike. The all-zero GUID is a valid GUID that means "not attributed".

**Why the column is `NOT NULL` with no default.** An audit column that can be skipped is
one that will be. The service writes the placeholder explicitly, so when auth arrives the
change is one line in `JobService.CreateAsync` and nothing else.

Neither field is published on `JobCreated` — putting a placeholder on the wire would be
handing consumers a value that means nothing. Adding them later is additive, which under
the contract's versioning rules does not change `eventVersion`.

---

## 8. Testing

**23 unit tests.** `dotnet test` from the repo root. All pass; solution builds with
0 warnings.

```
tests/JobService.Tests/
├── UnitTests/
│   ├── JobServiceTests.cs               15 tests  ← 11 facts + one 4-case theory
│   └── JobReferenceGeneratorTests.cs     7 tests
├── Fakes/
│   ├── FakeJobRepository.cs
│   ├── FakeAssetValidationClient.cs
│   ├── FakeEventPublisher.cs
│   └── MySqlExceptions.cs
└── UnitTest1.cs                          1 test   ← empty scaffold placeholder
```

```
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23, Duration: 70 ms
```

### The service tests

| Test | Asserts |
|---|---|
| Valid request creates job | success; `Id` is a real GUID, every field written, `status` CREATED, `scheduledDate` null, `createdBy` the zero GUID, reference matches `JOB-` + 6 |
| Validates before writing | both ids passed to the client, `ValidateAsyncCallCount == 1` |
| Each validation failure *(theory, 4 cases)* | the matching `ServiceError`, and **`CreateAsyncCallCount == 0`, `PublishAsyncCallCount == 0`** — a refusal leaves no trace |
| Reference collides once | success, `CreateAsyncCallCount == 2`, and the two attempted references **differ** |
| Reference collides twice | success on the third attempt, 3 distinct references |
| Every attempt collides | `MySqlException` propagates, nothing published |
| Publishes `JobCreated` | topic `job-created`, type `JobCreated`, version 1, **key == jobId**, full payload |
| **Publish fails → still succeeds** | `IsSuccess`, the job returned, `CreateAsyncCallCount == 1`, publish **attempted** |
| Reads the row back | response and event carry the database timestamps, not `default(DateTime)` |
| Validation unavailable | `AssetValidationUnavailableException` propagates; nothing written, nothing published |
| GetById / GetByReference | correct lookup used, null when absent |

The paired call-count assertions are the valuable ones, exactly as in US-02A: `== 0`
proves validation short-circuits before any write, `== 2` proves the retry actually
re-attempted rather than the fake lying about it.

`Fakes/MySqlExceptions.cs` is carried over from the customer service unchanged — a real
`MySqlException` built by reflection, because the service filters on
`catch (MySqlException ex) when (ex.Number == 1062)` and a stand-in type would take the
wrong branch and pass for the wrong reason.

### Generator tests

Format, length and alphabet are each asserted over **2000 draws**, not one — a single draw
proves nothing about a random generator. Plus: the alphabet is 32 *distinct* characters
(a repeat would keep the length right while skewing the draw), it excludes I/L/O/U, and
1000 draws produce 1000 distinct references.

### Verification table

| # | Criterion | How verified | Observed |
|---|---|---|---|
| 1 | A valid request creates a job and returns its reference | **Live** — 4 rows in `jobdb`, all with well-formed distinct references | ✅ `JOB-GCX43D`, `JOB-JM9TP4`, `JOB-N5TVMD`, `JOB-GV6ZD6` |
| 2 | An invalid asset/customer pairing is refused with a field-keyed 409 | **Unit tests only** (4-case theory) | ⚠️ not exercised over HTTP |
| 3 | The customer service being unreachable is a 503, not a 409 | **Unit tests only** | ⚠️ not exercised over HTTP |
| 4 | A `JobCreated` event is published, keyed on `jobId`, matching the contract | **Live** — console consumer from offset 0 | ✅ 2 events, correct envelope and payload, key == `jobId` |
| 5 | **A messaging failure must not invalidate the job** | **Live + unit test** | ✅ `JOB-N5TVMD` and `JOB-GV6ZD6` committed with no event — the request still succeeded |

Criterion 5 is the one this story turns on, and it is the one with the strongest evidence:
two jobs exist that no event ever announced, and both requests returned successfully. The
guarantee holds — and section 6 is the price of it.

> **Honesty note on the numbering.** Criterion 5 is named as such in the story brief.
> The numbering of 1–4 was not recorded in the session that produced this document, so
> those rows are labelled by behaviour and may not match the Jira wording. Reconcile
> against the ticket before sign-off.

Rows 2 and 3 are marked ⚠️ deliberately: they are covered by unit tests against fakes, but
**nobody has watched the real customer service return a 404, or watched it be down.** They
belong in the QA list below rather than being claimed as verified.

---

## 9. Handover to QA

Not covered by unit tests, and genuinely better as integration tests:

1. **The 409s over HTTP** — all four, against a real customer service. Specifically the
   inactive-customer path, which needs a customer deactivated *after* their asset was
   registered.
2. **The 503** — stop the customer service mid-flow and confirm a 503 with no row written.
   Then confirm the same request succeeds once it is back, unchanged.
3. **`JobRepository`** — all three methods against real MySQL, particularly the nullable
   `GetFieldValue<DateOnly>` read of `scheduled_date` and the `CHAR(36)`-as-Guid reads.
4. **The three CHECK constraints.** The `[RegularExpression]` attributes mirror them, so a
   bad value is a 400 long before MySQL sees it — the constraints themselves are untested.
5. **The unique index under concurrency** — simultaneous creates until a real 1062 fires.
   The retry is unit-tested with a fabricated exception; nobody has watched the index raise
   one.
6. **`MessageTimeoutMs`** — stop Kafka, create a job, and confirm the 201 arrives in about
   ten seconds rather than five minutes.
7. **The dual-write gap** — reproduce section 6 deliberately and confirm the log line is
   emitted, since the log is currently the only trace.

---

## 10. Running it locally

```powershell
# 1. Database and Kafka (platform repo)
cd assms-platform-infrastructure\docker\local
docker compose up -d
cd ..\..\kafka\docker
docker compose up -d

# 2. Migrations (job service repo)
.\scripts\development\apply_migrations.ps1 -Database jobdb -User job_svc -Password '<job_svc password>'

# 3. The customer service must be running — job creation validates against it
dotnet run --project src\CustomerAssetService        # http://localhost:5037

# 4. The job service
dotnet run --project src\JobService                  # http://localhost:5252

# 5. Frontend
npm run dev                                          # http://localhost:5173
```

Then `/jobs/new`, or "New job" in the nav.

Watch the events:

```powershell
docker exec assms-kafka /opt/kafka/bin/kafka-console-consumer.sh `
  --bootstrap-server localhost:29092 --topic job-created --from-beginning
```

Requires `appsettings.Development.json` (see `appsettings.Example.json`) and frontend
`.env` (see `.env.example`). Neither is committed.

**A job needs an active customer with at least one active asset.** On a fresh database,
register both first — the form says so rather than presenting empty dropdowns.

---

## 11. Decisions worth carrying forward

- **Cross-service reads go over HTTP, never a join.** Separate databases and separate
  credentials make it the only option, and that is a feature.
- **"Invalid" and "unavailable" are different failures.** The first is a verdict the user
  can act on (409); the second is the absence of one (503). Carry them in different types
  so no `switch` can confuse them.
- **Base URLs come from configuration with no fallback.** A fallback hides a missing
  config until it sends traffic somewhere unexpected.
- **Random identifiers over sequential** wherever a counter would need a lock — and drop
  the ambiguous characters from anything read aloud.
- **A 1062 is not always the user's fault.** Report it when the user supplied the value;
  retry it when the system drew it.
- **Publish after commit, and never fail a committed write because of a notification.**
  The row is the source of truth.
- **Say where the guarantee stops.** The dual-write gap is documented with evidence rather
  than glossed, because the next team will otherwise assume the event is reliable.
- **Placeholder values get a comment and a documented reason**, or the next reader files a
  bug against them.

---

## TODO before merge

- [ ] Jira id and branch name for this story
- [ ] Reconcile the criterion numbering in §8 against the ticket
- [ ] Settle `MessageTimeoutMs` — 10000 as built, versus the 5000 mentioned in the brief
- [ ] **Outbox story** — raise it before Dispatch consumes `job-created` in Sprint 2 (§6)
- [ ] `problem_description VARCHAR(1000)` — width chosen here, not specified. Confirm, or
      change via a `V02__` migration; `V01` is already applied
- [ ] Job list and detail pages — `GET /api/jobs/{id}` and the reference lookup both exist
      and are unused by the UI, which currently only creates
- [ ] Delete `tests/JobService.Tests/UnitTest1.cs`, the empty scaffold placeholder
- [x] Service category values — **`INSTALLATION`, `REPAIR`, `MAINTENANCE`, `INSPECTION`,
      `WARRANTY_CLAIM`**, mirrored between the CHECK constraint and the `[RegularExpression]`
- [x] Priority values — **`LOW`, `MEDIUM`, `HIGH`, `URGENT`**
- [x] Region values — **the nine Sri Lankan provinces**. Raised as a domain decision;
      confirm with the business whether the operation is province-wide or Colombo-only,
      because Dispatch depends on it in Sprint 2
- [x] Route path for the create-job page — **`/jobs/new`**
