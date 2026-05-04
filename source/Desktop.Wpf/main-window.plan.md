## Chunk A

### Goal

Ensure chat messages restored on app restart appear on the correct side (left/right) based on whether the author is the user or peer.

### Implementation Steps

#### A.1: Add `PeerId` to `IdentityRecord`

**File:** `Percolator.Identity/Model/IdentityRecord.cs`

Add the `PeerId` property using `Percolator.Identity.PeerId`:

```csharp
using Percolator.Identity;

public record IdentityRecord(Guid Id, string Name, string? Nickname = null)
{
    public SelfId SelfIdentityId { get; init; }
    public PeerId PeerId { get; init; }
}
```

**Note:** `PeerId` is defined in multiple domains (`Percolator.Identity`, `Percolator.Network`, `Percolator.Cryptography.Primitives`). For `IdentityRecord`, use `Percolator.Identity.PeerId`. Use fully qualified names or namespace aliases when conflicts occur (e.g., `using IdentityPeerId = Percolator.Identity.PeerId;`).

#### A.2: Add `PeerId` to `SelfIdentity` domain object

**File:** `Percolator.Identity/Model/SelfIdentity.cs`

Add the `PeerId` property:

```csharp
public sealed class SelfIdentity
{
    private readonly List<IdentityKey> _keys = new();

    public SelfId Id { get; }
    public DisplayName? DisplayName { get; private set; }
    public IReadOnlyList<IdentityKey> Keys => _keys;
    public DateTimeOffset LastUsedUtc { get; private set; }
    public PeerId PeerId { get; init; }

    public SelfIdentity(SelfId id)
    {
        Id = id;
        LastUsedUtc = default;
    }
```

#### A.3: Update repository to populate `PeerId` in `SelfIdentity`

**File:** `Percolator.Infrastructure/Identity/SqliteSelfIdentityDomainRepository.cs`

Update the `Map` method to populate `PeerId`:

```csharp
private static SelfIdentity Map(SelfIdentityDbo dbo)
{
    var self = new SelfIdentity(new SelfId(dbo.Id)) { PeerId = new PeerId(dbo.PeerId) };
    if (!string.IsNullOrWhiteSpace(dbo.Name)) self.SetDisplayName(dbo.Name);
    self.TouchLastUsed(dbo.LastUsedUtc);
    return self;
}
```

#### A.4: Update `IdentityOrchestrator` to use `PeerId` from `SelfIdentity`

**File:** `Percolator.Application/Identity/IdentityOrchestrator.cs`

Replace the random Guid generation with the actual PeerId from `SelfIdentity`:

```csharp
var peerId = dto.PeerId.Value;
var identityName = dto.DisplayName?.Value ?? dto.Id.ToString();
var identity = new IdentityRecord(peerId, identityName, null) with { SelfIdentityId = selfId, PeerId = dto.PeerId };
```

#### A.5: Update `ISelfParticipantIdProvider` to use `PeerId`

**File:** `Percolator.Application/Identity/ActiveIdentityContext.cs`

```csharp
ChatParticipantId ISelfParticipantIdProvider.Get()
{
    if (Identity is null)
    {
        throw new InvalidOperationException("Identity not loaded for chat participant.");
    }
    return new ChatParticipantId(Identity.PeerId.Value);
}
```

#### A.6: Update test mocks

Update the following test files to ensure `ISelfParticipantIdProvider.Get()` mock returns match the test's PeerId value:

- `Desktop.Wpf.Tests\ChatReloadCoordinatorTests.cs`
- `Percolator.Chat.Tests\PostDeliveredReceiptHandlerTests.cs`
- `Percolator.Chat.Tests\UpdateGroupMembershipHandlerTests.cs`
- `Percolator.Chat.Tests\AdminOperationsTests.cs`
- `Percolator.Chat.Tests\PostReadReceiptHandlerTests.cs`
- `Percolator.Chat.Tests\PostEmojiAnnotationHandlerTests.cs`
- `Percolator.ApplicationTests\Apps\Chat\GroupAdminHandlersTests.cs`
- `Percolator.ApplicationTests\Apps\Chat\GroupMembershipChangedHandlerTests.cs`

**Additional test updates:** Tests that create `SelfIdentity` objects directly must also be updated to provide PeerId when constructing SelfIdentity:

- `Percolator.IdentityTests\SelfIdentityTests.cs`
- `Percolator.InfrastructureTests\Identity\SelfIdentityDomainRepositoryTests.cs`
- `Desktop.Wpf.Tests\StartupIdentityServiceTests.cs`
- `Desktop.Wpf.Tests\ShellViewModelTests.cs`
- `Desktop.Wpf\Features\Self\StartupIdentityService.cs`

---