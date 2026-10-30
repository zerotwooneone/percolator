namespace Percolator.Infrastructure.Tests.Persistence.Core;

using System;
using System.IO;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;

[TestFixture]
public class BaselineStage2HostFactoryTests
{
    private string _tempDir = null!;
    private MasterKeyHandle _handle = null!;

    [SetUp]
    public void SetUp()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "stage2_host_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _handle = MasterKeyHandle.GenerateRandom();
    }

    [TearDown]
    public void TearDown()
    {
        _handle.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public void BuildHost_ResolvesDbContextAndFactoryWithKey()
    {
        // Arrange
        var options = Options.Create(new VaultStorageOptions { BaseDirectory = _tempDir });
        var factory = new BaselineStage2HostFactory(options);

        // Act
        using var host = factory.BuildAuthenticatedHost(_handle);
        using var scope = host.Services.CreateScope();

        // Assert
        var dbContext = scope.ServiceProvider.GetService<PercolatorDbContext>();
        dbContext.Should().NotBeNull();

        var dbContextFactory = scope.ServiceProvider.GetService<IDbContextFactory<PercolatorDbContext>>();
        dbContextFactory.Should().NotBeNull();

        // Assert Phase 2 services resolved
        scope.ServiceProvider.GetService<IIdentityProfileRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<ILocalIdentityKeyStore>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPrivatePreKeyStore>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IRatchetSessionRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IGroupSenderKeyRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IGroupReceiverSessionRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPendingHandshakeRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IGroupCredentialsRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IOutboxRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IOutboxQueryService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPeerContactRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPeerContactQueryService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IChannelRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IRelayPreKeyRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IRelayProfileRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IRelayLedgerRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPeerProfileRepository>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IPeerMailboxRoutingRepository>().Should().NotBeNull();
    }
}
