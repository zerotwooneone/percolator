// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Session;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;

namespace Percolator.Desktop.Tests.Common.Session;

[TestFixture]
public sealed class DesktopStage2SessionFactoryTests
{
    private string _tempDir = null!;
    private VaultStorageOptions _options = null!;
    private ServiceProvider _stage1Provider = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"percolator_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _options = new VaultStorageOptions
        {
            BaseDirectory = _tempDir
        };

        var stage1Services = new ServiceCollection();
        stage1Services.AddSingleton(Mock.Of<IWindowHandoffCoordinator>());
        stage1Services.AddSingleton(Mock.Of<IIdenticonGenerator>());
        stage1Services.AddSingleton(Mock.Of<Percolator.Desktop.Common.Services.IPortReservationService>());
        stage1Services.AddSingleton(Mock.Of<IClipboardService>());
        stage1Services.AddSingleton(TimeProvider.System);

        _stage1Provider = stage1Services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _stage1Provider.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task CreateSession_BuildsStage2Container_WithoutDependencyResolutionErrors()
    {
        var factory = new DesktopStage2SessionFactory(_stage1Provider, _options);
        using var masterKey = MasterKeyHandle.GenerateRandom();

        await using var session = factory.CreateSession(masterKey);

        session.Should().NotBeNull();
        // Assert that the previously failing services can be resolved cleanly
        var peerConnectionService = session.Services.GetRequiredService<IPeerConnectionService>();
        peerConnectionService.Should().NotBeNull();
        peerConnectionService.Should().BeOfType<DesktopPeerConnectionService>();

        var windowManager = session.Services.GetRequiredService<IWindowManager>();
        windowManager.Should().NotBeNull();

        var outboundWorker = session.Services.GetRequiredService<OutboundPendingRetryWorker>();
        outboundWorker.Should().NotBeNull();

        var mainWindowVm = session.Services.GetRequiredService<MainWindowViewModel>();
        mainWindowVm.Should().NotBeNull();

        var currentPersonaService = session.Services.GetRequiredService<ICurrentPersonaService>();
        currentPersonaService.Should().NotBeNull();
    }
}
