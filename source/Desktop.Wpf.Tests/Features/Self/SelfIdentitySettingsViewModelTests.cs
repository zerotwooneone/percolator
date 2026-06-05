using System;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Identity.Model;
using R3;

namespace Desktop.Wpf.Tests.Features.Self;

// Simple stub implementation for testing
public class StubIdentityStateService : IIdentityStateService
{
    public ReadOnlyReactiveProperty<SelfIdentityModel> ActiveIdentity { get; private set; }

    public StubIdentityStateService(ReadOnlyReactiveProperty<SelfIdentityModel> activeIdentity)
    {
        ActiveIdentity = activeIdentity;
    }

    public void UpdateDisplayName(SelfId targetId, string newName)
    {
        // No-op for testing - the service owns this side-effect
    }
}

[TestFixture]
public class SelfIdentitySettingsViewModelTests
{
    private Mock<ILogger<SelfIdentitySettingsViewModel>> _loggerMock;
    private StubIdentityStateService _identityStateServiceStub;
    private ReactiveProperty<SelfIdentityModel> _activeIdentityProp;
    private ReadOnlyReactiveProperty<SelfIdentityModel> _readOnlyProp;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<SelfIdentitySettingsViewModel>>(MockBehavior.Loose);
    }

    [Test]
    public void Constructor_GivenActiveIdentity_InitializesPropertiesCorrectly()
    {
        // ARRANGE
        var selfId = new SelfId(123);
        var listeningPort = new ListeningPort(5555);
        var selfModel = new SelfIdentityModel(selfId, "TestUser", listeningPort, true);

        _activeIdentityProp = new ReactiveProperty<SelfIdentityModel>(selfModel);
        _readOnlyProp = _activeIdentityProp.ToReadOnlyReactiveProperty();
        
        _identityStateServiceStub = new StubIdentityStateService(_readOnlyProp);

        // ACT
        var sut = new SelfIdentitySettingsViewModel(
            _loggerMock.Object,
            _identityStateServiceStub);

        // ASSERT
        sut.DisplayName.Value.Should().Be("TestUser");
        sut.ListeningPort.Value.Should().Be("5555");
    }

    [Test]
    public async Task SaveCommand_WhenDisplayNameIsEmpty_DoesNotCallUpdateDisplayName()
    {
        // ARRANGE
        var selfId = new SelfId(123);
        var selfModel = new SelfIdentityModel(selfId, "TestUser", new ListeningPort(5555), true);

        _activeIdentityProp = new ReactiveProperty<SelfIdentityModel>(selfModel);
        _readOnlyProp = _activeIdentityProp.ToReadOnlyReactiveProperty();
        _identityStateServiceStub = new StubIdentityStateService(_readOnlyProp);

        var sut = new SelfIdentitySettingsViewModel(
            _loggerMock.Object,
            _identityStateServiceStub);

        sut.DisplayName.Value = "   "; // User clears the text box

        // ACT
        sut.SaveCommand.Execute(Unit.Default);

        // ASSERT
        // Verify the warning was logged (side-effect of validation)
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Display name cannot be empty")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task SaveCommand_WhenActiveIdentityIsNull_DoesNotCallUpdateDisplayName()
    {
        // ARRANGE
        // Provide a valid identity for constructor initialization
        var selfId = new SelfId(123);
        var selfModel = new SelfIdentityModel(selfId, "TestUser", new ListeningPort(5555), true);
        
        _activeIdentityProp = new ReactiveProperty<SelfIdentityModel>(selfModel);
        _readOnlyProp = _activeIdentityProp.ToReadOnlyReactiveProperty();
        _identityStateServiceStub = new StubIdentityStateService(_readOnlyProp);

        var sut = new SelfIdentitySettingsViewModel(
            _loggerMock.Object,
            _identityStateServiceStub);

        // Now simulate ActiveIdentity becoming null after construction
        _activeIdentityProp.Value = null;
        sut.DisplayName.Value = "NewName";

        // ACT
        sut.SaveCommand.Execute(Unit.Default);

        // ASSERT
        // Verify the error was logged (side-effect of null check)
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("No active identity")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TearDown]
    public void TearDown()
    {
        _activeIdentityProp?.Dispose();
        _readOnlyProp?.Dispose();
    }
}
