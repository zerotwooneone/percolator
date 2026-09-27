### Milestone 1: Core Primitives, Test Doubles & Result Types
- Production: `IEntity<TId>`, `IDomainEvent`, `IAggregateRoot<TId>`, `AggregateRoot<TId>`, `IDateTimeProvider`, `ISensitiveSecret`, `DomainError`, `DomainResult`, `DomainResult<T>`.
- Test Double: `FakeDateTimeProvider`.
- Tests: `DomainResultTests`, `FakeDateTimeProviderTests`.

### Milestone 2: Identities Bounded Context (Multi-Tenancy, Personas & Devices)
- Production: `PublicIdentityId` (`[GuidId(CryptographicRandom)]`), `DeviceId`, `IdentityRole`, `IdentityState`, `DeviceLinkProof`, `IdentityPublicKey`, `IdentityProfile`, `DeviceRecord` (with signature verification), `PeerContact` (with phantom device defense and cap), `PreKeyBundleState`, repository ports (`IIdentityProfileRepository`, `IPeerContactRepository`, `IPreKeyStore`).
- Events: `IdentityDisabledEvent`.
- Tests: `IdentityProfileTests`, `DeviceRecordTests`, `PeerContactTests`.

### Milestone 3: Security Bounded Context (E2EE Ratchets & ZK Credentials)
- Production: `ChainKey`, `MessageKey`, `SharedSecret` (all implementing `ISensitiveSecret`), `ZkPresentationBytes`, `AuthCredentialMacBytes`, `ZkGroupPublicParams`, ports (`ICryptoEngine`, `IZkProofEngine`).
- Models: `DirectRatchetSession` (Double Ratchet with `StepDhRatchet` and LRU eviction of skipped keys), `GroupSenderKeyRatchet`.
- Test Doubles: `DeterministicCryptoEngine`, `FakeZkProofEngine`.
- Tests: `DirectRatchetSessionTests`, `GroupSenderKeyRatchetTests`.

### Milestone 4: Conversations Bounded Context (Semantic Chat & Group Invariants)
- Production: `ConversationId` (`[GuidId(CryptographicRandom)]`), `MessageId` (`[GuidId(SequentialTimeBased)]`), `EpochNumber`, `GroupRole`, `Message`, `GroupMember`, `DirectConversation`, `GroupConversation`, `IConversationRepository`.
- Events: `MessageAppendedEvent`, `MemberJoinedEvent`, `MemberRemovedEvent`.
- Tests: `GroupConversationTests`, `DirectConversationTests`.

### Milestone 5: Delivery Bounded Context (Relay Mailboxes & Ledger Hosting)
- Production: `BlindedRoutingToken` (`[GuidId(CryptographicRandom)]`), `EncryptedEntriesBlob`, `MailboxEnvelope`, `PurgePolicy`, `DeliveryToken`.
- Models: `RelayGroupLedger` (with Fiat-Shamir transcript binding and group capacity caps), `RelayMailboxQueue` (with `DeliveryToken` verification and quota enforcement).
- Ports: `IRelayLedgerRepository`, `IRelayMailboxRepository`.
- Events: `EpochCommittedEvent`.
- Tests: `RelayGroupLedgerTests`, `RelayMailboxQueueTests`, `GuidIdTests`.

*(Note: Store-and-Forward Outbox orchestration has been elevated to `Percolator.Application2` in accordance with Onion Architecture boundaries.)*
