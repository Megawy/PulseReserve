# ADR-001: Modular Monolith as Initial Architecture

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  PulseReserve is an enterprise-grade flash-sale and seat reservation platform designed for extreme concurrency, strict data consistency, and high traffic spikes. The platform spans multiple distinct bounded contexts: Catalog, Reservation, Payments, Identity, and Notifications. Microservices offer horizontal scale, but introduce operational complexity, network latency, distributed transaction orchestration (sagas), and deployment friction early in development.
* **Decision:**
  Adopt a **Modular Monolith** architecture with a 2-project Vertical Slice pattern per bounded context (`PulseReserve.Modules.{Module}` and `PulseReserve.Modules.{Module}.Contracts`), unified under a single host (`PulseReserve.AppHost`). Cross-module communication is restricted strictly to public contract projects (`.Contracts`). Internal module implementations are strictly isolated and verified by automated architecture tests (`NetArchTest.Rules`).
* **Rationale:**
  * Enforces bounded context boundaries in code without the overhead of independent network deployments.
  * Preserves single-process performance and transactional integrity within module boundaries.
  * Allows straightforward future extraction of any module (e.g. Reservation or Payments) into an independent microservice if scaling requirements justify it.
* **Alternatives Considered:**
  1. *Distributed Microservices:* Rejected due to premature operational overhead, network latency on the critical path, and complex distributed consistency requirements.
  2. *Traditional Layered Monolith:* Rejected due to high risk of cross-domain coupling, leaky abstractions, and lack of clear bounded context isolation.
* **Consequences:**
  * Positive: Fast local development, simple CI/CD, strictly enforceable boundary assertions via unit/architecture tests.
  * Negative: Requires engineering discipline and automated architecture test suites to prevent illegal references between module internals.
