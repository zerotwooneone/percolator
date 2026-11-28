<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Infrastructure/spec/repository.spec.md -->

# Infrastructure Repository Specification (`repository.spec.md`)

This specification governs all database repositories (`*Repository.cs`), query services (`*QueryService.cs`), and persistence adapters across `Percolator.Infrastructure`.

Any file governed by this specification SHOULD include this header comment on line 1:
```csharp
// SPECIFICATION GOVERNANCE: Read Percolator.Infrastructure/spec/repository.spec.md before modifying this file.
```

---

## 1. Quick Rules & Invariants

| ID | Category | MUST DO (Mandatory) | MUST NOT DO (Strictly Prohibited) |
| :-: | :--- | :--- | :--- |
| **[REPO-001]** | **Zero Captive Scope Facades (`IDbContextFactory` Standard)** | Persistence repositories and CQRS query services MUST directly inject `IDbContextFactory<TDbContext>`, create a short-lived DbContext per operation, and register as `Singleton`. | MUST NOT inject scoped `DbContext` directly into singletons, create captive scopes, or wrap scoped repositories in facade classes that manually manage `IServiceScopeFactory`. |
| **[REPO-002]** | **Pure Persistence Adapters** | Repositories MUST only persist and rehydrate state. Application orchestrators or domain services publish events after execution/commit. | MUST NOT publish domain or application events, emit message bus signals, or notify background workers from inside repository methods. |
| **[REPO-003]** | **Short-Lived DbContext Lifecycle** | Open a fresh context per operation via `await using var db = await _dbContextFactory.CreateDbContextAsync(ct);` and dispose immediately upon method exit. | MUST NOT store `DbContext` as a stateful instance field shared across concurrent calls or keep contexts open indefinitely. |
| **[REPO-004]** | **Explicit Atomic Transactions** | Wrap multi-table operations and mixed EF/raw SQL statements in an explicit transaction (`await using var tx = await db.Database.BeginTransactionAsync(ct);`), committing before disposal. | MUST NOT execute multi-table writes without an atomic transaction, leaving partial data on failure or crash. |
| **[REPO-005]** | **Aggregate Rehydration & Encapsulation** | Rehydrate domain aggregates cleanly from relational entities or raw records via factory methods (`Rehydrate`, `FromBytes`). All relational concepts remain encapsulated in Infrastructure. | MUST NOT leak EF Core entities, database foreign keys, or ORM tracking metadata outside the Infrastructure boundary. |
| **[REPO-006]** | **Native Database-Side Filtering** | Filter queries at the SQL layer via LINQ or parameterized SQL. Value conversions (e.g., UTC ticks for `DateTimeOffset`) are evaluated natively in SQL. | MUST NOT load unfiltered datasets into memory to perform client-side filtering (e.g. `ToListAsync()` followed by in-memory `.Where()`). |
| **[REPO-007]** | **Zero Unbounded In-Memory Fanout** | Restrict bulk reads using pagination, batching, or indexed filters. | MUST NOT query unlimited entity graphs across multiple tables into memory simultaneously. |
| **[REPO-008]** | **Zero Intermediate Allocations for Binary Lookups** | Deduplicate or index byte arrays using dedicated binary comparers (`ByteArrayComparer.Instance`) or strongly-typed value objects. | MUST NOT allocate hex strings (`Convert.ToHexString`) or Base64 strings in database read/write loops for hashing or deduplication. |
| **[REPO-009]** | **ChangeTracker Cleanliness & AsNoTracking** | Use `.AsNoTracking()` for all read-only queries and aggregate rehydrations. When executing raw ADO.NET SQL commands, rely on short-lived context disposal to prevent change tracker desynchronization. | MUST NOT rely on EF Core change tracking across disparate threads or mix raw SQL with tracked entities on long-lived contexts. |
| **[REPO-010]** | **Idempotent Persistence** | Ensure re-saving an aggregate or related records is idempotent. Immutable child tables (e.g., manifest files) MUST handle re-save without primary key collisions or duplicated rows. | MUST NOT assume `SaveAsync` is only called once per aggregate lifecycle; subsequent calls for state changes must not fail or corrupt state. |

---

## 2. Architectural Boundaries & Justification

### 2.1 Zero Captive Scope Facades (`IDbContextFactory` Standard) ([REPO-001])

In desktop and client applications, background workers, event listeners, and UI interactions execute concurrently across multiple threads. Managing Scoped services outside of standard HTTP request pipelines leads to two dangerous anti-patterns:
1. **Captive Scopes / Concurrency Collisions**: Injecting a scoped `DbContext` into a singleton or long-lived worker leads to concurrent access exceptions (`InvalidOperationException: A second operation was started on this context instance before a previous operation completed`).
2. **Scope Facade Anti-Pattern**: Creating wrapper facades that inject `IServiceScopeFactory`, create scopes on every method call, and resolve scoped repositories adds unnecessary indirection and hidden lifecycle dependencies.

```
BAD (Captive Scope Facade):
Caller ──► Singleton Facade ──► IServiceScopeFactory.CreateScope() ──► Scoped Repo ──► Scoped DbContext

GOOD (IDbContextFactory Standard):
Caller ──► Singleton Repo ──► IDbContextFactory.CreateDbContextAsync() ──► Short-Lived DbContext (Disposed)
```

#### Bad Implementation:
```csharp
// BAD: Facade manually managing scopes with fallback reflection hacks
public sealed class DesktopPeerContactQueryService : IPeerContactQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlPeerContactQueryService>();
        return await inner.GetContactSummariesAsync(id, ct);
    }
}
```

#### Good Implementation:
```csharp
// GOOD: Repository directly injects IDbContextFactory and creates context per operation; registered as Singleton
public sealed class SqliteTransferSessionRepository : ITransferSessionRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;

    public SqliteTransferSessionRepository(IDbContextFactory<PercolatorDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.TransferSessions.AsNoTracking().FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct);
        return entity == null ? null : ReconstituteSession(entity, ...);
    }
}
```

---

### 2.2 Pure Persistence Adapters ([REPO-002])

Repositories exist strictly to translate between domain aggregate representations and persistent storage.
- Repositories MUST NOT publish domain events or application bus notifications inside `SaveAsync`.
- Event publication belongs exclusively in the application layer (use-case handlers, orchestrators, or outbox dispatchers) after persistence succeeds.
- This guarantees transactional consistency and prevents events from being dispatched if the enclosing transaction aborts.

---

### 2.3 Short-Lived Contexts & ChangeTracker Cleanliness ([REPO-003], [REPO-009])

When persistence operations involve high data volume (such as large manifests with thousands of files) or combine EF Core entity mapping with high-performance raw ADO.NET SQL commands:
1. Every repository method creates its own `PercolatorDbContext` via `await using var db = await _dbContextFactory.CreateDbContextAsync(ct)`.
2. All read queries use `.AsNoTracking()` to eliminate unnecessary object tracking overhead.
3. Upon method completion, the context is disposed. Any raw SQL rows written directly to SQLite bypass the EF Core change tracker cleanly without risk of cached, stale entities interfering with future operations.

---

### 2.4 Native Database-Side Filtering ([REPO-006])

All query criteria MUST be pushed to the database engine.
- In `PercolatorDbContext`, properties of type `DateTimeOffset` are globally configured with `DateTimeOffsetToTicksConverter`, mapping them to 64-bit integer UTC ticks in SQLite.
- Consequently, LINQ predicates such as `s.CreatedAtUtc < createdBeforeUtc` translate directly to numeric SQL comparisons (`WHERE created_at_utc < @p0`).
- Filtering in memory via `.ToListAsync()` followed by LINQ-to-Objects `.Where()` wastes memory and IO bandwidth and is strictly prohibited.
