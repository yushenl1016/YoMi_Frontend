# Desktop community checks

Run `dotnet run --project tools/DesktopCommunityChecks --no-launch-profile` from the YoMi_Frontend directory. The complete run takes about 125 seconds because it exercises the real 120-second reconnect grace and 60-second idle timeout.

The project links the three production community files. It starts Kestrel on an ephemeral loopback port, uses actual HTTP and WebSocket clients, and stores announcements in a disposable SQLite database in the OS temporary directory. `FixtureDatabase.cs` replaces only the existing model/session boundary with fixture members and tokens. It does not read production appsettings, contact Google, or access the production SQL database. The fake session boundary opens real SQLite connections so the suite also detects database connections accidentally retained for a socket's lifetime.

Checks cover public announcements, administrator authorization, server authors/timestamps, length validation, revision conflicts, create/update/delete, persistence across a host restart, and retaining unrelated settings. Room checks cover bearer authentication, two clients, equipment ownership, scene bounds and expanded backgrounds, preset/custom avatars, music state, occupied/free seats, count-only keyboard reports, chat, capacity, malformed packets, member replacement, a delayed request from a replaced connection, reconnect counters/seats, signed host restoration, joiner retry, host transfer, revoked sessions, idle cleanup, and the full reconnect expiry.

The REST controller enforces HTTPS. The fixture-only host sets the request scheme to HTTPS over loopback to exercise those routes without installing a certificate. Production has no such override.

These checks validate the announcement service with a relational SQLite provider; SQL Server lock/deadlock behavior and the pre-existing SQL session verification require separate LocalDB/full-host checks. No cloud deployment or IIS configuration is changed.

## Wiring

- Register `DesktopAnnouncements` as scoped, `DesktopRooms` as singleton, and ASP.NET Data Protection.
- Enable WebSocket middleware before controller endpoints. Run one website worker for this in-memory room implementation.
- Persist Data Protection keys across website restarts under a stable application identity. A signed `resumeKey` allows only the authenticated room host to recreate a missing room; loss of keys requires creating a new room.
- Use `/api/desktop/rooms` with `Authorization: Bearer <desktop session>` in the WebSocket upgrade headers. Send JSON `{event,payload}`. Send `ping` at least every 20 seconds; `pong` includes `serverNow`.
- Preserve the complete `room:session` response (`code`, `memberId`, `token`, `host`, `resumeKey`) when sending `room:resume`. Stop automatic room retry on `session_replaced` or `auth_expired`; retry `room_missing` while the host reconnects.
- Announcement writes carry the list `revision` in their JSON body, including DELETE. The list is stored only in `FrontendSiteSetting.Key = desktop_pet_announcements`.

## Implementation decisions

- Use a serializable transaction for the setting document and administrator role read. Failed conflicts never replace saved content; a database deadlock/transient failure returns 503 for an explicit refresh/retry.
- Load room inventory from the authenticated member's existing profile and restrict broadcast equipment to owned catalog items. No client-supplied account ID controls membership.
- Use short-lived dependency scopes for socket authorization/inventory calls so idle sockets do not reserve SQL connection pool entries.
- Mark replaced connections retired before releasing the room lock. Their pending inventory requests cannot later mutate the room or replace their successor.

## Public/private directory checks

The complete run now includes `DirectoryChecks.cs`: isolated real-dispatcher tests with ephemeral protection keys, controlled sweep time, and in-memory WebSocket queues. Run the fast directory checks alone with `dotnet run --project tools/DesktopCommunityChecks -- --directory-only`.

Coverage includes private/default visibility and forged private-id joins, authoritative public fields, request correlation, failed-switch retention, a concurrent last-seat race, host rename/transfer, offline reservations and expiry, protected and legacy proof restoration, unsubscribe, current-membership recovery, and 6,700 rooms split into 67 ordered snapshot frames. The total snapshot exceeds 512 KiB while every frame remains below that transport limit. A saturated bounded outbox closes only the slow connection. The real WebSocket suite also checks capabilities, public-create delivery within one second, public joins, current-query recovery, and correlated identity rate-limit errors.

For cross-layer desktop integration, run `dotnet run --project tools/DesktopCommunityChecks --no-launch-profile -- --serve`. The test host prints `FIXTURE_URL=http://127.0.0.1:<port>` and waits for shutdown. Connect to `/api/desktop/rooms` using `fixture-1` or `fixture-2` bearer tokens (seeded names `Admin` and `Member`). Other positive fixture ids use the fallback member name. This mode uses the same disposable SQLite file and ephemeral keys; it never loads the production member store or credentials. Stop the host normally to dispose and remove the temporary database.

The directory protocol is version 1. Subscribe/unsubscribe use `rooms:subscribe` and `rooms:unsubscribe`. Snapshots carry `snapshotId`, `revision`, zero-based `part`, `last`, and at most 100 rows; ordered deltas carry `revision`, `upserts`, and `removed`. Rows expose only `publicId`, `title`, `hostName`, numeric Unix-millisecond `createdAt`, `count`, `onlineCount`, and `capacity`. Creation defaults to private when visibility is omitted. `room:join-public` accepts a public id; invitation joins continue to accept a code. `room:current` reattaches the authenticated member's existing room or returns `room:current-result` with `found:false`. New operation sessions/errors echo `requestId`; automatic host-promotion sessions have a null request id.
