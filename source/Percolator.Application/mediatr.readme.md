# MediatR in Percolator.Application

This document describes how to use MediatR in this codebase in a clean / hexagonal architecture.

## What MediatR is for (in this architecture)

MediatR is an **in-process application-layer message bus**. It is used to model and execute **application use cases** and to publish **application events**.

Use MediatR to:

- Implement **commands** (writes) that represent a single application intent.
- Implement **queries** (reads) only when the query is an application use case with orchestration (otherwise prefer a dedicated query interface).
- Publish **notifications** that describe an application-level fact that has already happened, so other application concerns can react.

### Key rule

- MediatR lives at the **application boundary**. It should be a mechanism for:
  - orchestrating domain work
  - coordinating infrastructure
  - coordinating UI-triggered use cases

It should not be used as a substitute for:

- domain services
- repositories
- state services
- reactive projections

## Command guidelines

Commands should:

- Be **explicit and intention-revealing**.
- Carry the **minimum data required** to execute the use case.
- Be **idempotent where reasonable** (or at least safe to retry).
- Validate invariants and produce a domain-meaningful result.

Commands should not:

- Return domain models intended to be mutated by callers.
- Expose persistence details (DbContext, entity types, EF tracking).
- Depend on WPF or UI threading concepts.

### Good examples

- `ApprovePendingSessionCommand(SelfId selfIdentityId, PendingSessionId pendingSessionId)`
- `RejectPendingSessionCommand(SelfId selfIdentityId, PendingSessionId pendingSessionId)`

These represent a clear application intent and keep identity scoping explicit.

## Query guidelines

- Generally do not use queries, use a query interface instead.

## Notification (event) guidelines

Notifications should:

- Be **facts about the past**, not requests.
- Be **small**, carrying IDs and minimal context.
- Avoid carrying domain aggregates, EF entities, or large payloads.
- Be safe to handle multiple times.

### Good examples

- `PendingSessionCreatedNotification(SelfId selfIdentityId, PendingSessionId pendingSessionId)`
- `PendingSessionRemovedNotification(SelfId selfIdentityId, PendingSessionId pendingSessionId, RequestCorrelationId requestCorrelationId, PendingSessionRemoveReason reason)`

These allow downstream handlers to:

- trigger reloads for the correct identity
- update UI state via existing reload coordinators/state services

## Patterns to avoid

### 1) Using MediatR as a UI event bus

Avoid publishing notifications just to make WPF update arbitrary viewmodels.

Bad:

- Notifications that contain UI-only concepts.
- Handlers that mutate viewmodels directly.

Preferred:

- Notifications trigger **reload coordinators** or **application state updates**.
- ViewModels project from canonical state services and marshal to UI thread.

### 2) Commands that exist only to "refresh" UI

Bad:

- `RefreshInboxCommand` / `ReloadEverythingCommand` as a replacement for correct state ownership.

Preferred:

- Commands mutate state.
- Notifications announce the mutation.
- Reload coordinators perform the minimal necessary reload.

### 3) Handlers that reach across layers or domains improperly

Bad:

- Application handlers referencing WPF assemblies.
- Domain types calling MediatR directly.

Preferred:

- Domain stays dependency-free.
- Application orchestrates domains.
- Infrastructure implements interfaces.
- Presentation consumes application interfaces.

### 4) Using MediatR to hide dependencies

Bad:

- Treating MediatR as a global service locator.
- Calling `_mediator.Send(...)` from deep inside domain models/services.

Preferred:

- Only application/presentation entry points initiate commands/queries.
- Dependencies are expressed via constructor injection.

### 5) Notifications that leak persistence concerns

Bad:

- Notifications carrying EF entities or tracked objects.
- Notifications that require a DbContext lifetime to be valid.

Preferred:

- Notifications carry identifiers and minimal context.
- Handlers explicitly query what they need.

## Practical checklist

When adding a new MediatR message:

- Is this a **use case** (command/query) or a **fact** (notification)?
- Are all fields **stable IDs or value objects**?
- Can a handler run without UI thread assumptions?
- Would a dedicated query interface be clearer/faster?
- Are you introducing an event bus when a state service + projection is the correct tool?
