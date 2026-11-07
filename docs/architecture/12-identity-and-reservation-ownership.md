# 12 – Identity and secure reservation ownership

Status: implemented (ADR-007 working baseline: same-origin cookie authentication).

## Design
- **Identity module** (`src/Modules/Identity`): own `identity` schema (ADR-008), `users` table, unique index on
  normalized email, PBKDF2 hashing through ASP.NET `PasswordHasher<T>` (versioned, rehash on login), lockout after
  5 failures for 15 minutes, uniform `Identity.InvalidCredentials` for unknown email / wrong password / locked account.
- **Session**: ASP.NET cookie authentication, HttpOnly, SameSite=Strict, `Secure` + `__Host-` prefix outside
  Development/Testing, absolute lifetime (default 8 h, no sliding), rejected when the user no longer exists.
- **CSRF**: antiforgery token bound to the signed-in user, validated on every POST of the identity and reservation
  APIs (403 `Csrf.Invalid`). Token is mirrored into the `XSRF-TOKEN` cookie for Angular.
- **Rate limiting**: register/login, fixed window per client IP (default 10/min).
- **Ownership**: reservation endpoints require the `Customer` policy (401 otherwise). `customerId` was removed from
  the hold body and from the get/release query strings; the owner is the session's user ID. Another user's reservation
  is reported as 404 (existence is not disclosed). Idempotency keys are scoped per user.

## Configuration
| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:Catalog` | none | required (shared database; `identity`, `catalog`, `reservation` schemas) |
| `DataProtection:KeysPath` | none | **required outside Development/Testing**; persistent, access-restricted directory. Startup fails without it |
| `Identity:SessionLifetimeMinutes` | 480 | 5–10080 |
| `Identity:RateLimit:PermitsPerMinute` | 10 | ≥ 1 |

Migrations: `dotnet ef database update` is NOT run by the host; apply the Identity migration (`InitialIdentity`)
explicitly to the intended database, like Catalog and Reservation.

## Known limits
No email verification or password reset.
Rate limiting keys on `RemoteIpAddress`; behind a reverse proxy configure forwarded headers.

## Roles, policies and the Admin trust boundary
| Role | How it is obtained | Policy |
|---|---|---|
| Customer | every registered user | `Customer`: reservations |
| Admin | listed in deployment configuration only | `Admin`: every Catalog mutation |

- **Admin is not data in the application.** There is no column, endpoint or request field that can grant it
  (registration ignores any `role`/`isAdmin` in the body; a test pins the identity route list). The trust boundary is
  whoever controls the deployment configuration `Identity:AdminUserIds`.
- **IDs, not emails.** The list holds user IDs (server-generated GUIDs). An email list could be pre-registered by an
  attacker before the real administrator signs up; a GUID cannot be chosen by anyone.
- **Roles are never read from the cookie.** On every request the principal is rebuilt from the database and the
  current list, so grants and revocations apply to existing sessions on their next request, without re-login.
- **Fail safe:** an unparsable, empty-GUID or email entry aborts startup; at runtime a damaged value grants nothing.

### Provisioning the first Admin
1. The future administrator registers normally in the app (or `POST /api/identity/register`) and signs in.
2. They read their user ID from `GET /api/identity/session` (`user.id`) while signed in (or the operator reads
   `identity.users.id` for their email in the database).
3. The operator sets `Identity__AdminUserIds=<that guid>` (several: comma separated) in the deployment environment
   or a protected config file, and restarts (a reloading config source such as appsettings.json applies it live).
4. The session endpoint now reports `isAdmin: true` and Catalog writes succeed. No password or secret is stored in
   configuration, only an ID.

### Rotating or revoking
Remove the ID from `Identity:AdminUserIds` (and restart unless the source reloads). The next request of any existing
session loses Admin. To rotate to a different person, add the new ID and remove the old one. To also end the old
person's login entirely, delete the user row (sessions of deleted users are rejected).

### Expected behaviour
| Request | Anonymous | Customer | Admin |
|---|---|---|---|
| `GET` catalog venues / layouts / event list / event details, `GET` availability | 200 | 200 | 200 |
| any Catalog `POST` (11 routes) | 401 `Auth.Unauthorized` | 403 `Auth.Forbidden` | 2xx/4xx by domain rules; needs CSRF token (403 `Csrf.Invalid`) |
| reservations | 401 | own only (others 404) | own only |

Authorization is evaluated before CSRF, so unauthenticated callers learn nothing about token validity. The Catalog
group applies the Admin policy to every non-GET route by convention, so a newly added write route is protected by
default; `CatalogSecurityTests` also fails if the route count changes without the test being updated.

### Protected Catalog mutation routes
`POST /api/catalog/venues`; `POST /api/catalog/seating-layouts` and `/{id}/sections`, `/rows`, `/seats`,
`/price-tiers`, `/lock`; `POST /api/catalog/events` and `/{id}/prices`, `/publish`, `/start-sale`.
No update, delete, unpublish or stop-sale routes exist yet; when added they inherit the policy.

### Production configuration
`Identity:AdminUserIds` (optional, validated), plus the settings above. Never put passwords, cookies or tokens in
configuration or logs; the application logs none of them.
