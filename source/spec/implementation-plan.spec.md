# Implementation Plan Specification (`implementation-plan-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

An **Implementation Plan** (`<Project>/<feature>-plan.md`) is a transient, executable work order that decomposes a feature or refactor into sequentially ticketed changes. Its purpose is to let an implementer — human or AI — execute each ticket **without discovering design, inventing names, or making architectural choices mid-implementation**. Design rationale lives in the QMS DAG (`Requirements.md`, `Design.md`, `DecisionRegister.md`); the plan exists to translate those baselines into ordered, verifiable edits.

A plan is **not** a QMS record: it introduces no semantic identifiers, sits outside the master traceability DAG, and is retired or archived when its tickets complete. It **MUST NOT** define, amend, or contradict `SYS-*`, `DEP-*`, `INV-*`, `DEC-*`, or `HAZ-*` items — it only *executes* them and links to them for traceability (ISO 13485 Clause 7.3.4 design-output discipline applied informally).

---

## 2. Form Rules — No Prose

1. **Work orders, not essays.** Plans are normative structures: tables, exact file lists, code blocks, and terse imperative bullets. Narrative rationale, background, and "why" paragraphs are prohibited — link the governing `DEC-*`/`INV-*`/`DEP-*` item instead.
2. **Exact technical detail, zero discovery.** Every name referenced in a ticket **MUST** be written exactly as it will exist (or exists) in source: file paths relative to repo root, type/method signatures, constructor argument lists, DI lifetimes (`AddSingleton`/`AddScoped`), `DomainError` codes, proto message/field names, and line-number hints for surgical edits (e.g., `TransitionToMainShellAsync` before `await session.StartAsync(ct)` at ~L77). If a ticket could force an implementer to grep for a type or guess a parameter order, the plan is under-specified.
3. **Contracts are code blocks.** Interface, VO, and method contracts **MUST** be shown as compilable C# — never described in prose. A contract bullet explains placement and semantics in one line at most.
4. **Verify-before-write.** File paths, type names, and signatures asserted by the plan **MUST** be checked against the current codebase when the plan is written or revised. Stale names are plan defects; fix the plan text in place.
5. **Real decision gates only.** "It is not a decision if there are no alternatives." Tickets **MUST NOT** stage pseudo-choices (Option A/B/C) for details with one sane answer. A choice is called out only when it has genuinely competing alternatives of architectural or protocol significance, and then as a decision block naming the options and the governing `DEC-*` (or a pending human sign-off). Routine implementation details are decided in the plan itself.
6. **One source of truth per construct — no duplication.** Each contract, VO, registration block, or mechanism is fully specified exactly once, in the earliest ticket that introduces it. Later tickets reference it by ticket ID or file path (`the Unwrap signature from DRAIN-04`, `the AddScoped(typeof(PublicIdentityId?), ...) registration`) — never restate the block. Likewise, the plan links QMS identifiers instead of restating requirement text. Stale copies of a duplicated contract diverge silently.
7. **Scope is explicit.** The plan states its goal in the title line and may list explicit **non-goals** when adjacent work tempts scope creep. Tickets execute in listed order; a ticket may not assume an outcome a later ticket has not yet produced.

---

## 3. Lazy-AI Pattern Checklist

Before a ticket is committed to the plan (and during any plan revision), sweep for these failure modes. Their presence means the plan is delegating thinking to implementation time.

- **Primitive obsession:** raw `string`, `Guid`, `byte[]`, `uint`, or bare tuples crossing a port, boundary, or aggregate seam where a domain VO exists or should (`string queueId` → `MailboxQueueId`; `uint signedPreKeyId` → `SignedPreKeyId`). The plan names the VO.
- **Sentinel ambiguity:** `(0, 0)` tuples, empty strings, or nulls used as state signals. Prefer `DomainResult<T>`, a `Found`/`IsValid` flag, or an explicit enum — and say which in the ticket.
- **Boundary leakage:** proto-generated types, EF entities, or SDK types appearing in Domain/Application contracts. Mapping happens at the adapter boundary; the plan names the VO/DTO the port actually exposes.
- **Convenience-member drift:** overloads or helpers added to a contract for one caller's ergonomics (e.g., a drain method parked on a provisioning interface). Interfaces expose the minimal semantic surface; per-caller convenience lives on the concrete class with a ticket to remove it.
- **Hand-waved mechanism:** verbs like "handle," "support," or "wire up" without the concrete mechanism (registration shape, call site, algorithm steps numbered 1–n).
- **Unverifiable verification:** "add tests" without naming the test file, the cases it covers, or the command that proves them.
- **Self-contradiction:** a later ticket that assumes an earlier ticket's outcome which the earlier ticket's text no longer produces (see §6 Hygiene).
- **Speculative generality:** abstractions, options parameters, or extension points specced "for future flexibility" that no ticket exercises. If no ticket needs it, it does not exist.
- **Silent call-site breakage:** a ticket that changes a shared contract without its **Files** block enumerating every implementer and call site — including mocks, fakes, and test adapters. Interface changes are never local.

---

## 4. Strict DDD Hexagonal Rules

1. **Dependency direction.** `Domain` defines VOs, aggregates, and domain ports; `Application` defines app-layer ports and services; `Infrastructure` implements them; composition roots (`Desktop`, `Simulator`, test hosts) wire them. Tickets **MUST NOT** invert this: no Domain/Application type references a proto, EF, or SDK type; no port signature carries a transport concern.
2. **Boundary mapping.** Adapters translate external types at the outermost edge (gRPC endpoint ↔ VO, EF row ↔ aggregate). The plan states which file performs each mapping.
3. **Identity is a call parameter, not captured state.** Services shared across personas/hosts **MUST** take per-identity values (e.g., `PublicIdentityId recipientId`) as method arguments — never as constructor state — so a single scoped/singleton registration serves every identity. Per-identity factories in composition roots are a smell reserved for genuinely per-identity resources.
4. **Optional boundary seams.** When one service/endpoint type serves hosts with different local capabilities (e.g., persona host vs. relay-only host), capability differences are injected as optional dependencies with an explicitly documented skip policy — not via subclassing or endpoint duplication. The plan states which composition root supplies each optional value and what happens when it is absent.
5. **DI registrations are code.** Registration calls appear verbatim in tickets (`services.AddScoped<ISealedEnvelopeUnwrapper, SealedEnvelopeUnwrapper>()`), including non-obvious mechanics (e.g., nullable service types require the `Type`-based `AddScoped(Type, factory)` overload — the generic `AddScoped<TService>` is `class`-constrained).
6. **Failure taxonomy.** Domain and application failures return `DomainResult<T>`/`DomainError` with stable error codes — never thrown exceptions; exceptions are reserved for infrastructure faults (I/O, transport, programming errors). Tickets name the error codes a path produces.
7. **Test adapters obey the same architecture.** Integration-test adapters and fakes implement the same port contracts; a contract change lists them in the ticket's **Files** block.

---

## 5. QMS Traceability

1. Each ticket ends with a `**QMS:**` line listing the identifiers it executes or constrains (`INV-*`, `DEC-*`, `DEP-*`, `SYS-*`, `HAZ-*`). Only identifiers the governing documents permit the deploying project to reference may be cited (per the traceability DAG in `qms-spec.md` §2).
2. Plans **MUST NOT** invent or redefine QMS items. If a ticket's work implies a missing or wrong requirement/invariant, that is a change-control event on the QMS document — flag it for human approval rather than encoding it in the plan.
3. When a ticket's shipped outcome changes the meaning of a QMS-referenced design choice, the plan's later tickets cite the *actual* shipped mechanism; do not silently reinterpret the identifier.

---

## 6. Plan Hygiene (Mandatory While Executing)

1. **Forward propagation.** Any deviation, naming change, or shipped outcome from an earlier ticket that affects later tickets **MUST** be written into the affected later tickets' text immediately. It is not sufficient to note the change in chat or assume a later step handles it.
2. **Marking done.** A ticket flips `⬜` → `✅` only after its **Verify** line passes. Completion leaves a one-line `**Verified:**` record (commands + counts) — nothing more.
3. **No shipped-notes narratives.** Deviations and forward-affecting constraints belong *inside the affected later tickets*, not in retrospective prose. A completed ticket may carry at most the `**Verified:**` line and a pointer to where a constraint moved forward.
4. **Stale-text repair.** Descriptions of not-yet-done work found to be wrong are corrected in place; do not layer corrections as caveats.

---

## 7. Verification Convention

1. Every ticket carries a `**Verify:**` line naming the new/updated test files and the exact commands that prove it (`dotnet test <project> --filter ...`).
2. Any ticket that modifies a simulation/test-harness host **MUST** additionally run that host's full scripted scenario suite before it may be marked done — plan-level DI and composition changes in those hosts are invisible to unit tests. The plan states the exact command. For `Percolator.Simulator`:

   ```powershell
   dotnet run --framework net10.0-windows10.0.19041.0 -- --replay Scenarios
   ```

   (From `Percolator.Simulator/`, or `--project` from `source/`; use `net10.0` on non-Windows. See `Percolator.Simulator/README.md`.)

3. A ticket is not done on a green build; the Verify line must execute.

---

## 8. Required Document Structure

An implementing plan **MUST** use this skeleton. Section explanations are not repeated inside the plan.

### `# <Feature> — Implementation Plan`
One-line status legend (`✅ done · 🚧 in progress · ⬜ not started`) followed by any plan-wide verification conventions (e.g., the simulator rule in §7.2).

### `## Architecture`
An inventory table of every type the plan introduces or relocates — `Layer | Type | Location | Lifetime` — so readers get the target shape before reading tickets.

### `## Tickets`
`### <PREFIX>-NN <status> — <title>` per ticket, each containing:

- **`Files:`** — `Create`/`Modify`/`Move`/`Delete` entries with full repo-relative paths and a parenthetical for surgical edits (method name, ~line, what changes). For contract changes this list is exhaustive: every implementer, call site, fake, mock, and test adapter.
- **Contract bullets / code blocks** — exact signatures, DI registrations, algorithms as numbered steps.
- **`**Verify:**`** — test files + commands (§7).
- **`**QMS:**`** — traceability identifiers (§5).

Optional: a decision block when a ticket contains a genuine architectural choice (§2.5) — options, chosen mechanism, rejected alternative, one line each.
