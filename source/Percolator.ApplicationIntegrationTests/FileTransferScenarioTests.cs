using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class FileTransferScenarioTests
{
    [Test]
    public async Task Phase7_Alice_Transfers_Large_Dataset_To_Bob_Over_MultiStream_Swarm()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Build cluster with Alice (15001) and Bob (15002) in a direct LAN connection
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];

        // Establish Direct 1:1 session between Alice and Bob
        var channelId = await alice.EstablishDirectSessionWithAsync(bob);
        cluster.Switch.HasActiveStream(alice.Port, bob.Port).Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Step 7.1: Alice stages a 3.5 MB file (14 x 256 KB chunks) in IDesktopFileStorage
        // ----------------------------------------------------------------------------------
        const int fileSizeBytes = (int)(3.5 * 1024 * 1024); // 3,670,016 bytes
        var originalData = new byte[fileSizeBytes];
        RandomNumberGenerator.Fill(originalData);
        var expectedSha256 = SHA256.HashData(originalData);

        var aliceSourceRoot = RootDirectoryPath.Parse(@"C:\AliceStorage");
        var relativePath = SanitizedRelativePath.Parse("large_dataset.bin");
        var aliceResolvedPath = ResolvedFilePath.Resolve(aliceSourceRoot, relativePath);
        alice.DesktopFileStorage.StageFile(aliceResolvedPath, originalData);

        // ----------------------------------------------------------------------------------
        // Step 7.2: Alice initiates transfer via driving port IFileTransferActionHandler
        // ----------------------------------------------------------------------------------
        var initiateResult = await alice.FileTransferActions.InitiateTransferAsync(
            channelId,
            alice.IdentityId,
            [bob.IdentityId],
            aliceSourceRoot,
            [relativePath],
            DirectWanPolicy.DisabledByDefault);

        initiateResult.IsSuccess.Should().BeTrue(initiateResult.Error.Description);
        var sessionId = initiateResult.Value;

        // Strict CQRS: Query Alice's session state via IFileTransferQueryService
        var aliceTransfer = await alice.FileTransferQueries.GetTransferByIdAsync(sessionId);
        aliceTransfer.Should().NotBeNull();
        aliceTransfer!.Status.Should().Be(TransferSessionState.Offered);
        aliceTransfer.Role.Should().Be(TransferRole.Sender);
        aliceTransfer.TotalBytes.Bytes.Should().Be(fileSizeBytes);
        aliceTransfer.TotalChunks.Count.Should().Be(14); // 3.5 MB / 256 KB = 14 chunks

        // ----------------------------------------------------------------------------------
        // Step 7.3: Bob receives in-band offer and accepts transfer via IFileTransferActionHandler
        // ----------------------------------------------------------------------------------
        // Strict CQRS: Bob queries pending transfer session
        var bobPendingTransfer = await bob.FileTransferQueries.GetTransferByIdAsync(sessionId);
        bobPendingTransfer.Should().NotBeNull();
        bobPendingTransfer!.Status.Should().Be(TransferSessionState.PendingConsent);
        bobPendingTransfer.Role.Should().Be(TransferRole.Recipient);
        bobPendingTransfer.InitiatorId.Should().Be(alice.IdentityId);
        bobPendingTransfer.TotalBytes.Bytes.Should().Be(fileSizeBytes);
        bobPendingTransfer.DownloadedBytes.Bytes.Should().Be(0);

        var bobDestRoot = RootDirectoryPath.Parse(@"C:\BobStorage");
        var acceptResult = await bob.FileTransferActions.AcceptTransferAsync(sessionId, bobDestRoot);
        acceptResult.IsSuccess.Should().BeTrue(acceptResult.Error.Description);

        // Strict CQRS: After acceptance, Bob's transfer transitions to InFlight
        var bobInFlightTransfer = await bob.FileTransferQueries.GetTransferByIdAsync(sessionId);
        bobInFlightTransfer.Should().NotBeNull();
        bobInFlightTransfer!.Status.Should().Be(TransferSessionState.InFlight);

        // ----------------------------------------------------------------------------------
        // Step 7.4: Concurrently pump multi-stream chunks across connected stream pool
        // ----------------------------------------------------------------------------------
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pumpTask = Task.WhenAll(
            alice.PumpTransferAsync(sessionId, bob.IdentityId, cts.Token),
            bob.PumpTransferAsync(sessionId, alice.IdentityId, cts.Token));

        await pumpTask;

        // ----------------------------------------------------------------------------------
        // Step 7.5: Bob verifies completed manifest and byte-for-byte SHA-256 integrity
        // ----------------------------------------------------------------------------------
        // Strict CQRS: Bob asserts completed transfer state
        var bobFinalTransfer = await bob.FileTransferQueries.GetTransferByIdAsync(sessionId);
        bobFinalTransfer.Should().NotBeNull();
        bobFinalTransfer!.Status.Should().Be(TransferSessionState.Completed);
        bobFinalTransfer.DownloadedBytes.Bytes.Should().Be(fileSizeBytes);
        bobFinalTransfer.CompletedChunks.Count.Should().Be(bobFinalTransfer.TotalChunks.Count);
        bobFinalTransfer.CompletedAtUtc.Should().NotBeNull();

        // Strict CQRS: Bob asserts completed manifest file projection
        var bobFiles = await bob.FileTransferQueries.GetManifestFilesAsync(sessionId);
        bobFiles.Should().ContainSingle();
        var completedFile = bobFiles[0];
        completedFile.RelativePath.Value.Should().Be("large_dataset.bin");
        completedFile.IsCompleted.Should().BeTrue();
        completedFile.IsSelected.Should().BeTrue();
        completedFile.FileSize.Bytes.Should().Be(fileSizeBytes);

        // Assert byte-for-byte fidelity against Alice's original file
        var bobResolvedPath = ResolvedFilePath.Resolve(bobDestRoot, relativePath);
        var bobFileBytes = bob.DesktopFileStorage.GetFileBytes(bobResolvedPath);
        bobFileBytes.Should().NotBeNull();
        bobFileBytes!.Length.Should().Be(fileSizeBytes);

        var bobSha256 = SHA256.HashData(bobFileBytes);
        bobSha256.Should().Equal(expectedSha256, "Transferred dataset on Bob's node must match Alice's original byte-for-byte");

        // Strict CQRS: Alice asserts uploaded metrics
        var aliceFinalTransfer = await alice.FileTransferQueries.GetTransferByIdAsync(sessionId);
        aliceFinalTransfer.Should().NotBeNull();
        aliceFinalTransfer!.UploadedBytes.Bytes.Should().Be(fileSizeBytes);
    }

    [Test]
    public async Task Phase7_Selective_File_Transfer_Downloads_Only_Selected_Files()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Alice (15001) and Bob (15002)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];

        var channelId = await alice.EstablishDirectSessionWithAsync(bob);

        // Alice stages a multi-file bundle:
        // File 0: readme.txt (64 KB)
        // File 1: dataset.bin (512 KB)
        var readmeData = new byte[64 * 1024];
        var datasetData = new byte[512 * 1024];
        RandomNumberGenerator.Fill(readmeData);
        RandomNumberGenerator.Fill(datasetData);

        var aliceSourceRoot = RootDirectoryPath.Parse(@"C:\AliceMultiFile");
        var readmeRelPath = SanitizedRelativePath.Parse("docs/readme.txt");
        var datasetRelPath = SanitizedRelativePath.Parse("data/dataset.bin");

        alice.DesktopFileStorage.StageFile(ResolvedFilePath.Resolve(aliceSourceRoot, readmeRelPath), readmeData);
        alice.DesktopFileStorage.StageFile(ResolvedFilePath.Resolve(aliceSourceRoot, datasetRelPath), datasetData);

        var initiateResult = await alice.FileTransferActions.InitiateTransferAsync(
            channelId,
            alice.IdentityId,
            [bob.IdentityId],
            aliceSourceRoot,
            [readmeRelPath, datasetRelPath],
            DirectWanPolicy.DisabledByDefault);

        initiateResult.IsSuccess.Should().BeTrue();
        var sessionId = initiateResult.Value;

        // Bob queries manifest files before accepting (Strict CQRS)
        var manifestFiles = await bob.FileTransferQueries.GetManifestFilesAsync(sessionId);
        manifestFiles.Should().HaveCount(2);

        // Bob selectively accepts ONLY File 0 (readme.txt)
        var selectedOnlyReadme = SelectedFileIndices.FromIndices([FileIndex.FromInt(0)], FileCount.FromInt(manifestFiles.Count));
        var bobDestRoot = RootDirectoryPath.Parse(@"C:\BobSelective");

        var acceptResult = await bob.FileTransferActions.AcceptTransferAsync(
            sessionId,
            bobDestRoot,
            selectedFiles: selectedOnlyReadme);
        acceptResult.IsSuccess.Should().BeTrue();

        // Concurrently pump streams
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(
            alice.PumpTransferAsync(sessionId, bob.IdentityId, cts.Token),
            bob.PumpTransferAsync(sessionId, alice.IdentityId, cts.Token));

        // Strict CQRS: Bob asserts File 0 completed, File 1 was NOT downloaded
        var bobFilesAfter = await bob.FileTransferQueries.GetManifestFilesAsync(sessionId);
        bobFilesAfter.Should().HaveCount(2);

        var readmeFile = bobFilesAfter.First(f => f.RelativePath.Value == "docs/readme.txt");
        readmeFile.IsSelected.Should().BeTrue();
        readmeFile.IsCompleted.Should().BeTrue();

        var datasetFile = bobFilesAfter.First(f => f.RelativePath.Value == "data/dataset.bin");
        datasetFile.IsSelected.Should().BeFalse();
        datasetFile.IsCompleted.Should().BeFalse();

        // Verify Bob's storage only has readme.txt and not dataset.bin
        var bobReadmePath = ResolvedFilePath.Resolve(bobDestRoot, readmeRelPath);
        var bobReadmeBytes = bob.DesktopFileStorage.GetFileBytes(bobReadmePath);
        bobReadmeBytes.Should().NotBeNull();
        bobReadmeBytes!.Should().Equal(readmeData);

        var bobDatasetPath = ResolvedFilePath.Resolve(bobDestRoot, datasetRelPath);
        bob.DesktopFileStorage.GetFileBytes(bobDatasetPath).Should().BeNull();
    }
}
