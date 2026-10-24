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
