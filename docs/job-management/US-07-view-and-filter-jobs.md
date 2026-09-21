# US-07 — View and Filter Jobs

## API

`GET /api/jobs` returns the Job Service job list. A response row includes the job reference, status, priority and the locally stored assignment context when the job has been assigned.

Supported optional query parameters are:

- `status`: `CREATED` or `ASSIGNED` (case-insensitive input).
- `assignedTechnicianId`: a technician GUID.

When both parameters are present, they use **AND** semantics. A valid filter with no matching jobs returns `200 OK` and `[]`. An unsupported status or malformed technician ID returns `400 Bad Request` with a `ValidationProblemDetails` response keyed to the invalid query parameter.

`GET /api/jobs/{id}` supplies the detail view opened by selecting a list row.

## Access control

`GET /api/jobs`, `GET /api/jobs/{id}` and `GET /api/jobs/reference/{jobReference}` require a valid staff JWT with the `Dispatcher` or `Manager` role. Creating a job requires the `Agent` or `Manager` role. Swagger exposes the Bearer security scheme and returns `401` for an absent, invalid or expired token and `403` when the staff role is not permitted.

The Job Service staging environment file at `/etc/assms/job.env` must contain the same shared values used by Customer & Asset and Dispatch:

```env
Authentication__Jwt__Issuer=assms-customer-asset-service
Authentication__Jwt__Audience=assms-internal
Authentication__Jwt__SigningKey=<shared staging signing key>
```

The signing key must be at least 32 UTF-8 bytes. It belongs only in the protected runtime environment file, never in this repository or a GitHub variable.

## Assignment boundary

The Job Service reads assignments only from the `jobs` table in **jobdb**. `JobAssigned` events update this local projection, and duplicate events are handled by the existing idempotent event-consumer logic. The list and detail endpoints do not query `dispatchdb` or any other service database.

## Dispatcher UI

The frontend provides `/jobs` and `/jobs/{id}` for Dispatcher and Manager roles. The list allows status and assigned-technician filtering, displays an empty result clearly, and preserves active filters in the URL so the result can be shared or refreshed.
