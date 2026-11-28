// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using R3;

[TestFixture]
public sealed class ChatViewModelFileTransferAndIntroTests
{
    private ChannelId _channelId;
    private PublicIdentityId _myPersonaId;
    private PublicIdentityId _remotePeerId;
    private Mock<IChatService> _chatServiceMock = null!;
    private Mock<IChatMessageQueryService> _queryServiceMock = null!;
    private FakeConversationStateService _conversationStateService = null!;
    private ChatDraftStateService _draftStateService = null!;
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private Subject<ActivePersonaModel?> _personaChangedSubject = null!;
    private FakeTimeProvider _timeProvider = null!;
    private Mock<IFileTransferActionHandler> _actionHandlerMock = null!;
    private Mock<IFileTransferQueryService> _ftQueryServiceMock = null!;
    private Mock<IDialogService> _dialogServiceMock = null!;
    private Mock<IFilePickerService> _filePickerMock = null!;
    private Mock<IPeerIntroductionConsentRepository> _introRepoMock = null!;
    private SynchronizationContext? _previousSyncContext;
    private string _tempDir = null!;

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [SetUp]
    public void SetUp()
    {
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

        _channelId = ChannelId.New();
        _myPersonaId = PublicIdentityId.New();
        _remotePeerId = PublicIdentityId.New();

        _chatServiceMock = new Mock<IChatService>();
        _queryServiceMock = new Mock<IChatMessageQueryService>();
        _conversationStateService = new FakeConversationStateService();

        _personaChangedSubject = new Subject<ActivePersonaModel?>();
        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _currentPersonaServiceMock.Setup(p => p.CurrentPersona)
            .Returns(new ActivePersonaModel(_myPersonaId, "Alice", 5124));
        _currentPersonaServiceMock.Setup(p => p.CurrentPersonaChanged)
            .Returns(_personaChangedSubject);

        _draftStateService = new ChatDraftStateService(_currentPersonaServiceMock.Object, NullLogger<ChatDraftStateService>.Instance);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        _actionHandlerMock = new Mock<IFileTransferActionHandler>();
        _ftQueryServiceMock = new Mock<IFileTransferQueryService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _filePickerMock = new Mock<IFilePickerService>();
        _introRepoMock = new Mock<IPeerIntroductionConsentRepository>();

        _queryServiceMock.Setup(q => q.GetMessagesAsync(
                _channelId,
                It.IsAny<ulong?>(),
                It.IsAny<int>(),
                It.IsAny<ulong>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageReadModel>());

        _conversationStateService.RegisterDirectConversation(_remotePeerId, "Bob", _channelId);

        _tempDir = Path.Combine(Path.GetTempPath(), "PercolatorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _personaChangedSubject.Dispose();
        _conversationStateService.Dispose();
        _draftStateService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private ChatViewModel CreateViewModel()
    {
        return new ChatViewModel(
            _channelId,
            _chatServiceMock.Object,
            _queryServiceMock.Object,
            _conversationStateService,
            _draftStateService,
            _currentPersonaServiceMock.Object,
            _timeProvider,
            NullLogger<ChatViewModel>.Instance,
            NullLogger<ChatReloadCoordinator>.Instance,
            _actionHandlerMock.Object,
            _ftQueryServiceMock.Object,
            _dialogServiceMock.Object,
            _filePickerMock.Object,
            _introRepoMock.Object);
    }

    [Test]
    public async Task SendFile_IPDisclosureDeclined_DoesNotOpenPickerNorInitiateTransfer()
    {
        // Arrange: User declines disclosure
        _dialogServiceMock.Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DialogResult.None);

        using var vm = CreateViewModel();

        // Act
        await vm.SendFileCommand.ExecuteAsync(null);

        // Assert
        _dialogServiceMock.Verify(d => d.ShowAsync(
            It.Is<DialogRequest>(r => r.Title.Contains("IP Disclosure") && r.Message.Contains("reveals your IP address")),
            It.IsAny<CancellationToken>()), Times.Once);

        _filePickerMock.Verify(p => p.PickFilesAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _actionHandlerMock.Verify(a => a.InitiateTransferAsync(
            It.IsAny<ChannelId>(),
            It.IsAny<PublicIdentityId>(),
            It.IsAny<IReadOnlyList<PublicIdentityId>>(),
            It.IsAny<RootDirectoryPath>(),
            It.IsAny<IReadOnlyList<SanitizedRelativePath>>(),
            It.IsAny<DirectWanPolicy>(),
            It.IsAny<RootBundleName>(),
            It.IsAny<SeedRatio>(),
            It.IsAny<bool>(),
            It.IsAny<IProgress<TransferPreparationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SendFile_IPDisclosureAccepted_InitiatesTransferWithVerbatimNotice()
    {
        // Arrange: User accepts disclosure
        DialogRequest? capturedRequest = null;
        _dialogServiceMock.Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DialogRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(DialogResult.Primary);

        var testFile = Path.Combine(_tempDir, "document.pdf");
        await File.WriteAllTextAsync(testFile, "test data");

        _filePickerMock.Setup(p => p.PickFilesAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { testFile });

        var expectedSessionId = TransferSessionId.New();
        _actionHandlerMock.Setup(a => a.InitiateTransferAsync(
                _channelId,
                _myPersonaId,
                It.Is<IReadOnlyList<PublicIdentityId>>(r => r.Contains(_remotePeerId)),
                It.IsAny<RootDirectoryPath>(),
                It.IsAny<IReadOnlyList<SanitizedRelativePath>>(),
                DirectWanPolicy.ExplicitlyAllowed,
                It.IsAny<RootBundleName>(),
                null,
                false,
                It.IsAny<IProgress<TransferPreparationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<TransferSessionId>.Success(expectedSessionId));

        using var vm = CreateViewModel();

        // Act
        await vm.SendFileCommand.ExecuteAsync(null);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Message.Should().Contain("Direct P2P file transfer connects directly to Bob (and group swarm peers) without relaying through the Exchange. This reveals your IP address to participating peers.");

        _actionHandlerMock.Verify(a => a.InitiateTransferAsync(
            _channelId,
            _myPersonaId,
            It.Is<IReadOnlyList<PublicIdentityId>>(r => r.Contains(_remotePeerId)),
            It.IsAny<RootDirectoryPath>(),
            It.IsAny<IReadOnlyList<SanitizedRelativePath>>(),
            DirectWanPolicy.ExplicitlyAllowed,
            It.IsAny<RootBundleName>(),
            null,
            false,
            It.IsAny<IProgress<TransferPreparationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AcceptTransfer_IPDisclosureDeclined_DoesNotOpenFolderPickerNorAccept()
    {
        var sessionId = TransferSessionId.New();
        _dialogServiceMock.Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DialogResult.None);

        using var vm = CreateViewModel();

        // Act
        await vm.AcceptTransferAsync(sessionId);

        // Assert
        _dialogServiceMock.Verify(d => d.ShowAsync(
            It.Is<DialogRequest>(r => r.Message.Contains("reveals your IP address")),
            It.IsAny<CancellationToken>()), Times.Once);

        _filePickerMock.Verify(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _actionHandlerMock.Verify(a => a.AcceptTransferAsync(It.IsAny<TransferSessionId>(), It.IsAny<RootDirectoryPath>(), It.IsAny<SelectedFileIndices>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AcceptTransfer_IPDisclosureAccepted_AcceptsTransfer()
    {
        var sessionId = TransferSessionId.New();
        _dialogServiceMock.Setup(d => d.ShowAsync(It.IsAny<DialogRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DialogResult.Primary);

        _filePickerMock.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_tempDir);

        _actionHandlerMock.Setup(a => a.AcceptTransferAsync(sessionId, It.IsAny<RootDirectoryPath>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        using var vm = CreateViewModel();

        // Act
        await vm.AcceptTransferAsync(sessionId);

        // Assert
        _actionHandlerMock.Verify(a => a.AcceptTransferAsync(
            sessionId,
            It.Is<RootDirectoryPath>(p => p.Value == _tempDir),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StopSeeding_CallsActionHandler()
    {
        var sessionId = TransferSessionId.New();
        _actionHandlerMock.Setup(a => a.StopSeedingAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        using var vm = CreateViewModel();

        // Act
        await vm.StopSeedingAsync(sessionId);

        // Assert
        _actionHandlerMock.Verify(a => a.StopSeedingAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PauseAndResumeAndCancel_CallActionHandler()
    {
        var sessionId = TransferSessionId.New();
        _actionHandlerMock.Setup(a => a.PauseTransferAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());
        _actionHandlerMock.Setup(a => a.ResumeTransferAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());
        _actionHandlerMock.Setup(a => a.CancelTransferAsync(sessionId, TransferTerminationReason.SenderCancelled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        using var vm = CreateViewModel();

        await vm.PauseTransferAsync(sessionId);
        _actionHandlerMock.Verify(a => a.PauseTransferAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);

        await vm.ResumeTransferAsync(sessionId);
        _actionHandlerMock.Verify(a => a.ResumeTransferAsync(sessionId, It.IsAny<CancellationToken>()), Times.Once);

        await vm.CancelTransferAsync(sessionId);
        _actionHandlerMock.Verify(a => a.CancelTransferAsync(sessionId, TransferTerminationReason.SenderCancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task VaultLock_TeardownCancelsActiveTransfers()
    {
        var activeSessionId = TransferSessionId.New();
        var model = new TransferSessionReadModel(
            activeSessionId,
            RootBundleName.Parse("active_bundle.tar"),
            _myPersonaId,
            new TransferTopology.IncomingStaged(_myPersonaId),
            null,
            TotalBundleBytes.FromBytes(1000),
            TotalBundleBytes.FromBytes(500),
            TotalBundleBytes.FromBytes(0),
            SeedRatio.FromMultiplier(0),
            SeedRatio.FromMultiplier(1.0),
            CompletedChunkCount.FromInt(5),
            ChunkCount.FromInt(10),
            TransferBandwidth.FromBytesPerSecond(100),
            TransferBandwidth.FromBytesPerSecond(0),
            StreamCount.Default,
            PeerCount.FromInt(1),
            TransferSessionState.InFlight,
            TransferRole.Sender,
            DateTimeOffset.UtcNow,
            null,
            _channelId);

        _ftQueryServiceMock.Setup(q => q.GetAllTransfersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { model });

        using var vm = CreateViewModel();
        await vm.RefreshTransfersAsync();

        var transferList = (IReadOnlyList<FileTransferItemViewModel>)vm.Transfers;
        transferList.Count.Should().Be(1);
        transferList[0].IsInFlight.Should().BeTrue();

        // Act: trigger vault lock (persona cleared)
        _personaChangedSubject.OnNext(null);

        // Assert: CancelTransferAsync invoked
        _actionHandlerMock.Verify(a => a.CancelTransferAsync(activeSessionId, TransferTerminationReason.SenderCancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RequestIntroduction_DispatchesConsentCommand()
    {
        var charlieId = PublicIdentityId.New();
        var charlieChannelId = ChannelId.New();
        _conversationStateService.RegisterDirectConversation(charlieId, "Charlie", charlieChannelId);

        using var vm = CreateViewModel();

        vm.AvailableCandidatesForIntro.Should().Contain(c => c.DisplayName == "Charlie");
        var charlieCandidate = vm.AvailableCandidatesForIntro.First(c => c.DisplayName == "Charlie");
        vm.SelectedCandidateForIntro = charlieCandidate;
        vm.IntroNoteText = "Hey Bob, meet Charlie!";

        _chatServiceMock.Setup(c => c.RequestIntroductionConsentAsync(It.IsAny<RequestIntroductionConsentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<string>.Success("req-999"));

        // Act
        await vm.RequestIntroductionConsentCommand.ExecuteAsync(null);

        // Assert
        _chatServiceMock.Verify(c => c.RequestIntroductionConsentAsync(
            It.Is<RequestIntroductionConsentCommand>(cmd =>
                cmd.ChannelId == _channelId &&
                cmd.RequesterId == _myPersonaId &&
                cmd.CandidatePeerId == charlieId &&
                cmd.CandidateDisplayName == "Charlie" &&
                cmd.ForwardChannelId == charlieChannelId &&
                cmd.Note == "Hey Bob, meet Charlie!"),
            It.IsAny<CancellationToken>()), Times.Once);

        vm.IntroNoteText.Should().BeEmpty();
    }

    [Test]
    public async Task InboundIntroduction_AllowAndDecline_DispatchesResponses()
    {
        var candidateId = PublicIdentityId.New();
        var consent = new PeerIntroductionConsent(
            requestId: "req-inbound-1",
            channelId: _channelId,
            localIdentityId: _myPersonaId,
            remotePeerId: _remotePeerId,
            candidatePeerId: candidateId,
            candidateDisplayName: "Dave",
            note: "Meet Dave",
            direction: IntroductionDirection.InboundRequest,
            status: IntroductionStatus.Pending,
            createdAtUtc: DateTimeOffset.UtcNow);

        _introRepoMock.Setup(r => r.GetPendingInboundRequestsAsync(_myPersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { consent });

        _chatServiceMock.Setup(c => c.RespondToIntroductionConsentAsync(It.IsAny<RespondToIntroductionConsentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        using var vm = CreateViewModel();
        await vm.RefreshPendingIntroductionsAsync();

        vm.HasPendingIntroduction.Should().BeTrue();
        vm.PendingIntroductionText.Should().Contain("Dave");
        vm.PendingIntroductionRequestId.Should().Be("req-inbound-1");

        // Act: Allow
        await vm.AllowPendingIntroductionCommand.ExecuteAsync(null);

        // Assert: response sent
        _chatServiceMock.Verify(c => c.RespondToIntroductionConsentAsync(
            It.Is<RespondToIntroductionConsentCommand>(r => r.RequestId == "req-inbound-1" && r.Allowed == true),
            It.IsAny<CancellationToken>()), Times.Once);

        vm.HasPendingIntroduction.Should().BeFalse();
    }
}
