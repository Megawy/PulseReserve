# 23. Reverse proxy, rate limiting and simulator isolation

Evidence labels are those of document 19. Everything here is **U** (executed unit tests, including an in-memory ASP.NET Core test host with the
real middleware) or **S**; nothing was run behind a real proxy or CDN, and no browser was used.

## 1. Client address and scheme behind a proxy

### The problem

The sign-in limiter is keyed by client address. Behind a reverse proxy every request arrives from the proxy's address, so one client could
exhaust the limit for everyone. Reading `X-Forwarded-For` blindly is worse: any client could choose its own address and evade the limit.

### Configuration (required outside Development and Testing)

| Setting | Meaning |
| --- | --- |
| `Security:ForwardedHeaders:Mode` | **`Direct`** - clients reach the application themselves; `X-Forwarded-*` headers are **ignored**. **`Proxy`** - a reverse proxy sits in front; the address and scheme come from the headers **only from listed proxies**. Required in every environment except Development and Testing (where it defaults to `Direct`); startup fails if it is missing there. The application does not guess its topology. |
| `Security:ForwardedHeaders:KnownProxies` | comma/semicolon separated IP addresses of the proxies |
| `Security:ForwardedHeaders:KnownNetworks` | comma/semicolon separated CIDR networks (e.g. `10.0.0.0/8`) |
| `Security:ForwardedHeaders:ForwardLimit` | 1-10, default **1**: only the nearest proxy hop is believed. Raise it only for a chain whose every hop is listed. |

Startup **fails** for: a missing mode (outside Development/Testing); a mode other than `Direct`/`Proxy`; `Direct` with proxies or networks listed
(contradictory); `Proxy` with none listed (that would trust every sender); a catch-all network (`/0`); an entry that is not an IP or CIDR; a forward
limit outside 1-10 (U: `ForwardedHeadersTests`, 16 tests).

Loopback is **not** trusted implicitly: if the proxy runs on the same host, list `127.0.0.1` / `::1`. In `Direct` mode forwarded headers are
ignored even from loopback (U).

Example (nginx, a Docker network, one hop):

```
Security__ForwardedHeaders__Mode=Proxy
Security__ForwardedHeaders__KnownNetworks=172.18.0.0/16
```

### Middleware order (`Program.cs`)

`UsePulseReserveForwardedHeaders` -> exception handler (outside Development) -> `UseAuthentication` -> `UseRateLimiter` -> `UseAuthorization` -> endpoints.

* Forwarded headers come **first**, so the limiter, logs and `Request.Scheme` see the real client (U: `TheSignInLimit_IsPerRealClient_BehindATrustedProxy`,
  `AnAttackerRotatingForwardedForValues_StaysInTheSingleBucketOfItsRealAddress`, `AnUntrustedPeer_CannotChooseItsOwnAddressOrScheme`).
* Authentication runs **before** the limiter so that the per-user financial limit can use the signed-in user. Before this change the limiter ran
  first and could not see the user.
* Authorization runs after the limiter, so a request that authorization will refuse (`401/403`) still consumes a permit of its endpoint's policy, but never reaches a handler.

## 2. Rate limits

| Policy | Key | Default | Applies to |
| --- | --- | --- | --- |
| `auth` | **client address** (after forwarded-header processing) | 10 per minute (`Identity:RateLimit:PermitsPerMinute`) | register, sign-in |
| `financial-write` | **signed-in user id**; an anonymous request (which authorization refuses with `401` anyway) falls into one bucket per client address | 60 per minute (`Security:FinancialWritesPerMinute`, >= 1) | create order, start payment, cancel, every administrator POST (retry, refund request, manual refund, exception and anomaly resolution, process-now), simulator completion |

Both answer `429` with a problem body that says nothing was changed; the console and checkout show "Too many requests. Nothing was changed."
(U: `FinancialWrites_AreLimitedPerSignedInUser_AdministratorsToo_AndReadsAreNot`, `CustomerPaymentStarts_AreLimitedPerUser`,
`TheFinancialWriteLimit_HasASafeDefault_AndIsConfigurable`, `AFinancialWriteLimitBelowOne_FailsStartup`).

**Webhooks are deliberately not rate limited by the application** (U: `Webhooks_AreNotThrottledByTheFinancialWriteLimit_...`). A throttle on
`/api/webhooks/*` would cause a real provider's legitimate retries and bursts to be answered `429`, which can disable the endpoint at the provider
or delay a payment confirmation. A webhook is protected by its signature (checked on the raw body before anything is read) and a 256 KiB body cap.
If a per-address limit or an allow-list of provider networks is wanted, configure it in the proxy.

## 3. Log redaction of the Paymob signature (outside the application)

Paymob authenticates its callback with an `hmac` **query parameter**. The application removes it from the request before the hosting layer logs the
request line (`WebhookRequestRedaction`, U). Anything *in front of* the application is outside its control:

* configure the reverse proxy, CDN, WAF and load balancer **not to log, or to mask, the query string of `/api/webhooks/*`** (nginx: log `$uri`
  instead of `$request_uri`; Cloudflare/other CDNs: exclude or hash the query in log fields; cloud load balancers: disable or mask the query in
  access logs);
* the same applies to any request-tracing or APM product that records URLs.

A signature in a log is not a secret key (it is valid only for that body) but it is part of what authenticates the callback and must not be kept.
**Not verified**: no proxy was configured or inspected in this work.

## 4. Simulator isolation: fail closed

The payment simulator confirms payments by itself; it must never be reachable in production. It is decided in one place
(`PaymentsOptions.ResolveSimulator`) and every other part of the host reads its answer.

| Environment name (compared case-insensitively, otherwise exact) | `Payments:Simulator:Enabled` absent or `false` | `true` alone | `true` **and** `Payments:Simulator:AllowInTestingEnvironment=true` |
| --- | --- | --- | --- |
| `Development` | off | **on** (everyday workflow, unchanged) | on |
| `Testing` | off | **startup fails** (the message names both keys) | on |
| anything else: `Production`, `Staging`, `Prod`, `Test`, `Live`, a misspelling, an unset or blank name | off | startup fails | **startup fails** - the opt-in never rescues another environment |

Backed by U: `ProviderConfigurationTests` (the simulator section, with 18 cases added for the Testing opt-in, among them `ADeployedHostNamedTesting_CannotStartTheSimulator_WithoutTheExplicitOptIn`,
`TheTestingOptIn_NeverRescuesAnotherEnvironment`, `TheTestingOptIn_IsWhatAutomatedTestsSet_AndItAloneEnablesNothing`) and the Docker-free
integration-project tests in `PaymentsConfigurationTests` (executed).

Further rules:

* Both settings must be literally `true` or `false`: a typo (`tru`, `yes`, `1`, `on`) **fails startup in every environment**, even when the
  simulator is otherwise off, rather than being read as off or on.
* The simulator **cannot run beside a real provider** in any environment, including Development: enabling it while `Payments:Stripe:Enabled` or
  `Payments:Paymob:Enabled` is true fails startup, so a copied settings file cannot give a host a self-confirming payment method next to a real one.
* **No silent fallback.** A real provider that is enabled but misconfigured is simply *unavailable* (the reasons are logged, never the values), the
  simulator stays off, and a payment start answers 503. Asking for both is refused outright, however broken the real provider is (U:
  `AnIncompleteRealProvider_IsNeverReplacedBySilentlyStartingTheSimulator`, `WithNothingConfigured_NoPaymentMethodExists_AndNothingFallsBackToTheSimulator`).
* When the simulator is on, the host logs a Warning at startup naming the environment, and `GET /api/admin/payments/providers` lists it as simulated.
  The completion endpoint is not mapped otherwise; it is a customer-owned, antiforgery-protected, rate-limited route.
* The development notification adapter keeps its previous rule (Development/Testing only); it delivers nothing and moves no money.
* Disabling the simulator proves nothing about a real provider: no provider sandbox call has been made (document 16, checklist).

### Which variables to set where

| Scenario | Environment | Settings |
| --- | --- | --- |
| Local development with Docker (default) | `Development` (set by `compose.dev.yaml`) | `SIMULATOR_ENABLED=true` in `.env` (the default) is passed as `Payments__Simulator__Enabled`; nothing else |
| Local `dotnet run` | `Development` | `appsettings.Development.json` already enables the simulator |
| A real provider's test mode, locally | `Development` | `SIMULATOR_ENABLED=false` together with `STRIPE_ENABLED=true` / `PAYMOB_ENABLED=true` and the test keys (document 16); without Docker set `Payments__Simulator__Enabled=false` to override the development file. Real providers accept only test credentials outside `Production`, so no real money moves |
| Integration tests | `Testing` (set by `CatalogApiFactory`) | the factory sets `Payments__Simulator__Enabled=true` **and** `Payments__Simulator__AllowInTestingEnvironment=true`, and switches the payments worker off. Do not set the opt-in anywhere else |
| Unit tests | none | fakes and in-memory hosts; no configuration |
| A deployment with a real provider (staging/production) | `Production` (or another non-development name) | **no** `Payments__Simulator__*` variable at all; `Payments__Stripe__*` / `Payments__Paymob__*` (document 16); `Security__ForwardedHeaders__Mode`; `DataProtection__KeysPath`; `Identity__AdminUserIds`; the database role from document 24 |

**Residual risk, reduced but not removed:** a host that was deployed under the name `Testing` can no longer start the simulator without also
carrying the explicit opt-in, so a mis-named production host fails at startup instead of silently accepting fake payments. `Testing` still relaxes
two **other** things that this change did not touch: `SecurityOptions` treats it like Development (no `Secure` cookie, no persisted data-protection
keys required, forwarded-header mode defaults to `Direct`), because the integration suite depends on it. Deployment checks should therefore still
assert that `ASPNETCORE_ENVIRONMENT` is neither `Development` nor `Testing`.

## 5. Deployment checklist for this section

1. Decide `Direct` or `Proxy` and set `Security:ForwardedHeaders:Mode` (+ proxies/networks). **A deployment that does not set it will not start.**
2. List only the proxies you operate; keep `ForwardLimit` at 1 unless you have a chain.
3. Make the proxy overwrite (not append to) client-supplied `X-Forwarded-*` headers and strip them from untrusted callers.
4. Mask `?hmac=` in proxy/CDN/WAF/LB/APM logs for `/api/webhooks/*`.
5. Optionally rate limit or allow-list `/api/webhooks/*` at the proxy.
6. Verify `ASPNETCORE_ENVIRONMENT` is not `Development` or `Testing` and that `Payments:Simulator:Enabled` is absent.
7. Tune `Security:FinancialWritesPerMinute` to the expected administrator/customer usage (the default 60 is per user, not per address).
