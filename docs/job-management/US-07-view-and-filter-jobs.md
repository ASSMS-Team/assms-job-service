# US-07 — View and Filter Jobs

## API

`GET /api/jobs` returns the Job Service job list. A response row includes the job reference, status, priority and the locally stored assignment context when the job has been assigned.

Supported optional query parameters are:

- `status`: `CREATED` or `ASSIGNED` (case-insensitive input).
- `assignedTechnicianId`: a technician GUID.

When both parameters are present, they use **AND** semantics. A valid filter with no matching jobs returns `200 OK` and `[]`. An unsupported status or malformed technician ID returns `400 Bad Request` with a `ValidationProblemDetails` response keyed to the invalid query parameter.

`GET /api/jobs/{id}` supplies the detail view opened by selecting a list row.

## Assignment boundary

The Job Service reads assignments only from the `jobs` table in **jobdb**. `JobAssigned` events update this local projection, and duplicate events are handled by the existing idempotent event-consumer logic. The list and detail endpoints do not query `dispatchdb` or any other service database.

## Dispatcher UI

The frontend provides `/jobs` and `/jobs/{id}` for Dispatcher and Manager roles. The list allows status and assigned-technician filtering, displays an empty result clearly, and preserves active filters in the URL so the result can be shared or refreshed.
