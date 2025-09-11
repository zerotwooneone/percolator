using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.App.Handlers;
using Percolator.Chat.ValueObjects;
using MediatR;

namespace Percolator.Chat.Tests;

[TestFixture]
public class ApplySignedAdminOperationHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IConversationRepository> _repository = null!;
    private Mock<ISelfParticipantIdProvider> _selfProvider = null!;
    private Mock<IGroupAdminKeyStore> _adminKeyStore = null!;
    private Mock<IGroupAdminOpStore> _adminOpStore = null!;
    private Mock<IAdminSignatureVerifier> _sigVerifier = null!;
    private Mock<IMediator> _mediator = null!;

    private static Conversation MakeConversation(out ParticipantId p1, out ParticipantId p2)
    {
        p1 = new ParticipantId(Guid.NewGuid());
        p2 = new ParticipantId(Guid.NewGuid());
        return new Conversation(new ConversationId(Guid.NewGuid()), new[] { p1, p2 }, Array.Empty<Message>(), null);
    }

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _repository = new Mock<IConversationRepository>(MockBehavior.Strict);
        _selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
        _adminKeyStore = new Mock<IGroupAdminKeyStore>(MockBehavior.Strict);
        _adminOpStore = new Mock<IGroupAdminOpStore>(MockBehavior.Strict);
        _sigVerifier = new Mock<IAdminSignatureVerifier>(MockBehavior.Strict);
        _mediator = new Mock<IMediator>(MockBehavior.Strict);
    }

    private ApplySignedAdminOperationHandler CreateHandler()
        => new ApplySignedAdminOperationHandler(_resolver.Object, _repository.Object, _selfProvider.Object, _adminKeyStore.Object, _adminOpStore.Object, _sigVerifier.Object, _mediator.Object);

    [Test]
    public async Task ValidSignature_GrantAdmin_AddsKey()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation(out var p1, out var p2);
        var selfIdentityId = 1;
        var opId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;
        var grantee = new AdminPublicKey(new byte[] {1,2,3});

        _resolver.Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _adminOpStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sentAt, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(true);

        // One valid admin key at that time
        _adminKeyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new[] {
                          new GroupAdminKeyRecord(new AdminPublicKey(new byte[]{9}), sentAt.AddMinutes(-5), null)
                      });

        _sigVerifier.Setup(v => v.VerifyAsync(It.IsAny<AdminPublicKey>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);

        _adminKeyStore.Setup(s => s.AddKeyAsync(convo.Id.Value, grantee, sentAt, It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var cmd = new ApplySignedAdminOperationCommand(
            lookup,
            opId,
            sentAt,
            null,
            AdminOperationKind.GrantAdmin,
            grantee,
            null,
            null,
            null,
            null,
            null,
            new AdminSignature(new byte[]{5}),
            new byte[]{7}
        );

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _adminOpStore.VerifyAll();
        _adminKeyStore.VerifyAll();
        _sigVerifier.VerifyAll();
        _repository.VerifyNoOtherCalls();
    }

    [Test]
    public async Task InvalidSignature_Throws()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation(out var p1, out var p2);
        var selfIdentityId = 1;
        var opId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;

        _resolver.Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _adminOpStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sentAt, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(true);

        // One key but signature fails
        _adminKeyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new[] {
                          new GroupAdminKeyRecord(new AdminPublicKey(new byte[]{9}), sentAt.AddMinutes(-5), null)
                      });

        _sigVerifier.Setup(v => v.VerifyAsync(It.IsAny<AdminPublicKey>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);

        var handler = CreateHandler();
        var cmd = new ApplySignedAdminOperationCommand(
            lookup,
            opId,
            sentAt,
            null,
            AdminOperationKind.UpdateGroupInfo,
            null,
            null,
            null,
            null,
            "name",
            null,
            new AdminSignature(new byte[]{5}),
            new byte[]{7}
        );

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _resolver.VerifyAll();
        _adminOpStore.VerifyAll();
        _adminKeyStore.VerifyAll();
        _sigVerifier.VerifyAll();
    }

    [Test]
    public async Task Idempotent_SecondApplication_NoOp()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation(out var p1, out var p2);
        var selfIdentityId = 1;
        var opId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;

        _resolver.Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _adminOpStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sentAt, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(false); // already applied

        var handler = CreateHandler();
        var cmd = new ApplySignedAdminOperationCommand(
            lookup,
            opId,
            sentAt,
            null,
            AdminOperationKind.UpdateGroupInfo,
            null,
            null,
            null,
            null,
            "name",
            null,
            new AdminSignature(new byte[]{5}),
            new byte[]{7}
        );

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _adminOpStore.VerifyAll();
        _adminKeyStore.VerifyNoOtherCalls();
        _sigVerifier.VerifyNoOtherCalls();
        _repository.VerifyNoOtherCalls();
    }
}
