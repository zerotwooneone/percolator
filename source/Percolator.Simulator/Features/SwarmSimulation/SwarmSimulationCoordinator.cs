using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Ports;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Simulator.Features.DirectChat;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal sealed class AsyncManualResetEvent
{
    private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncManualResetEvent(bool initialState)
    {
        if (initialState)
        {
            _tcs.TrySetResult(true);
        }
    }

    public Task WaitAsync(CancellationToken ct = default)
    {
        var tcs = _tcs;
        if (tcs.Task.IsCompleted) return Task.CompletedTask;
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    public void Set()
    {
        _tcs.TrySetResult(true);
    }

    public void Reset()
    {
        while (true)
        {
            var tcs = _tcs;
            if (!tcs.Task.IsCompleted || Interlocked.CompareExchange(ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), tcs) == tcs)
            {
                return;
            }
        }
    }
}

internal sealed class ActiveSwarmParticipant
{
    public SimulatedNodeId NodeId { get; }
    public string DisplayName { get; }
    public SwarmPeerRole Role { get; set; }
    public PieceState[] Pieces { get; set; }
    public int CompletedChunks { get; set; }
    public int TotalChunks { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public bool IsPaused { get; set; }
    public bool IsCancelled { get; set; }
    public bool IsComplete { get; set; }
    public AsyncManualResetEvent PauseGate { get; } = new(initialState: true);

    public ActiveSwarmParticipant(
        SimulatedNodeId nodeId,
        string displayName,
        SwarmPeerRole role,
        int totalChunks,
        long totalBytes)
    {
        NodeId = nodeId;
        DisplayName = displayName;
        Role = role;
        TotalChunks = totalChunks;
        TotalBytes = totalBytes;
        Pieces = new PieceState[totalChunks];
        if (role == SwarmPeerRole.Seeder)
        {
            Array.Fill(Pieces, PieceState.Verified);
            CompletedChunks = totalChunks;
            DownloadedBytes = totalBytes;
            IsComplete = true;
        }
    }
}

internal sealed class ActiveSwarmState
{
    public Guid SessionId { get; }
    public SimulatedNodeSummary Summary { get; }
    public SimulatedNodeId InitiatorId { get; }
    public string InitiatorName { get; }
    public GeneratedFilePreset Preset { get; }
    public int TotalChunks { get; }
    public int ChunkSize { get; }
    public List<ChunkHash> LeafHashes { get; }
    public List<ActiveSwarmParticipant> Participants { get; } = new();
    public CancellationTokenSource Cts { get; } = new();
    public double LateJoinerThreshold { get; set; } = 0.5;
    public SimulatedNodeId? PendingLateJoinerId { get; set; }
    public bool LateJoinerInjected { get; set; }

    public ActiveSwarmState(
        Guid sessionId,
        SimulatedNodeSummary summary,
        SimulatedNodeId initiatorId,
        string initiatorName,
        GeneratedFilePreset preset,
        int totalChunks,
        int chunkSize,
        List<ChunkHash> leafHashes)
    {
        SessionId = sessionId;
        Summary = summary;
        InitiatorId = initiatorId;
        InitiatorName = initiatorName;
        Preset = preset;
        TotalChunks = totalChunks;
        ChunkSize = chunkSize;
        LeafHashes = leafHashes;
    }

    public ActiveSwarmParticipant? GetParticipant(SimulatedNodeId nodeId)
    {
        lock (Participants)
        {
            return Participants.FirstOrDefault(p => p.NodeId == nodeId);
        }
    }
}

internal sealed class SwarmSimulationCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatorLogSink _logSink;
    private readonly IStatusToastService _toastService;
    private readonly SyntheticFilePresetGenerator _presetGenerator;
    private readonly IFlightRecorderStateService _flightRecorder;

    private readonly ConcurrentDictionary<Guid, ActiveSwarmState> _activeSwarms = new();

    public IReadOnlyCollection<ActiveSwarmState> ActiveSwarms => _activeSwarms.Values.ToList();

    public SwarmSimulationCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        ISimulatorLogSink logSink,
        IStatusToastService toastService,
        SyntheticFilePresetGenerator presetGenerator,
        IFlightRecorderStateService flightRecorder)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _presetGenerator = presetGenerator ?? throw new ArgumentNullException(nameof(presetGenerator));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
    }

    public ActiveSwarmState? GetSwarm(Guid sessionId)
    {
        _activeSwarms.TryGetValue(sessionId, out var swarm);
        return swarm;
    }

    public ActiveSwarmState? GetSwarmByNodeId(SimulatedNodeId nodeId)
    {
        return _activeSwarms.Values.FirstOrDefault(s =>
            s.Summary.Id == nodeId ||
            s.Participants.Any(p => p.NodeId == nodeId));
    }

    public ActiveSwarmState? GetLatestSwarm()
    {
        return _activeSwarms.Values.LastOrDefault();
    }

    public async Task<Guid> InitiateTransferAsync(
        SimulatedNodeId initiatorNodeId,
        IReadOnlyList<SimulatedNodeId> recipientNodeIds,
        string presetSize = SyntheticFilePresetGenerator.Preset2Mb,
        CancellationToken ct = default)
    {
        var initiator = _worldState.GetPersonaHost(initiatorNodeId);
        if (initiator == null)
        {
            _toastService.ShowToast("Initiator Not Found", "Initiator persona is not active in world state.", ToastSeverity.Error);
            return Guid.Empty;
        }

        var recipients = recipientNodeIds
            .Select(id => _worldState.GetPersonaHost(id))
            .Where(h => h != null)
            .Cast<ISimulatedPersonaHost>()
            .ToList();

        if (recipients.Count == 0)
        {
            _toastService.ShowToast("Recipient Required", "At least one target recipient must be selected.", ToastSeverity.Warning);
            return Guid.Empty;
        }

        // Explicit file transfer privacy opt-in
        initiator.AllowFileTransfers = true;
        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            initiator.DisplayName,
            initiator.Id,
            "PrivacyPolicy",
            FileTransferIngressFilter.PrivacyNotice));

        // 1. Generate deterministic synthetic file on disk
        string presetsDir = Path.Combine(initiator.NodeTempDirectory, "Presets");
        GeneratedFilePreset preset = _presetGenerator.GeneratePreset(presetsDir, presetSize);

        // Discover loopback candidate via INetworkEndpointDiscoveryPort and initiate in-band transfer if ratchet session exists
        using (var initScope = initiator.Services.CreateScope())
        {
            var endpointDiscovery = initScope.ServiceProvider.GetRequiredService<INetworkEndpointDiscoveryPort>();
            var candidates = await endpointDiscovery.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault, ct);
            foreach (var candidate in candidates)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    initiator.DisplayName,
                    initiator.Id,
                    "EndpointDiscovery",
                    $"Discovered local loopback candidate: {candidate.Host.Value}:{candidate.Port.Value} (Kind: {candidate.Kind})"));
            }

            var ratchetRepo = initScope.ServiceProvider.GetRequiredService<IRatchetSessionRepository>();
            var actionHandler = initScope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            foreach (var r in recipients)
            {
                var session = await ratchetRepo.GetSessionAsync(initiator.PublicIdentityId, r.PublicIdentityId, DeviceId.Primary, ct);
                if (session != null)
                {
                    var channelId = ChannelIdUtility.DeterministicFor(initiator.PublicIdentityId, r.PublicIdentityId);
                    var sourceRoot = RootDirectoryPath.Parse(presetsDir);
                    var sanitizedPath = SanitizedRelativePath.Parse(preset.RelativeFileName);
                    var initResult = await actionHandler.InitiateTransferAsync(
                        channelId,
                        initiator.PublicIdentityId,
                        [r.PublicIdentityId],
                        sourceRoot,
                        [sanitizedPath],
                        DirectWanPolicy.DisabledByDefault,
                        ct: ct);

                    if (initResult.IsSuccess)
                    {
                        await initiator.FlushOutboxAsync(ct);
                    }
                    else
                    {
                        _logSink.Log(new SimulatorLogEntry(
                            DateTimeOffset.UtcNow,
                            LogLevel.Warning,
                            initiator.DisplayName,
                            initiator.Id,
                            "SwarmFileTransfer",
                            $"In-band transfer initiation warning: {initResult.Error!.Description}"));
                    }
                }
            }
        }

        // 2. Compute Merkle leaf hashes for all chunks
        const int chunkSize = 128 * 1024; // 128 KB standard chunk
        int totalChunks = (int)Math.Max(1, (preset.TotalBytes + chunkSize - 1) / chunkSize);

        List<ChunkHash> leafHashes = new(totalChunks);
        using (var fs = File.OpenRead(preset.FilePath))
        {
            byte[] chunkBuf = new byte[chunkSize];
            for (int i = 0; i < totalChunks; i++)
            {
                int read = fs.Read(chunkBuf, 0, (int)Math.Min(chunkSize, preset.TotalBytes - (long)i * chunkSize));
                leafHashes.Add(MerkleTreeBuilder.HashLeaf(chunkBuf.AsSpan(0, read)));
            }
        }

        Guid sessionId = Guid.NewGuid();
        var swarmNodeId = SimulatedNodeId.CreateNew();
        var swarmSummary = new SimulatedNodeSummary(
            swarmNodeId,
            $"Swarm: {preset.RelativeFileName}",
            SimulatedNodeType.Swarm,
            $"{initiator.DisplayName} ➔ {string.Join(", ", recipients.Select(r => r.DisplayName))} ({presetSize})");

        var swarmState = new ActiveSwarmState(
            sessionId,
            swarmSummary,
            initiatorNodeId,
            initiator.DisplayName,
            preset,
            totalChunks,
            chunkSize,
            leafHashes);

        // Add Seeder participant
        swarmState.Participants.Add(new ActiveSwarmParticipant(
            initiatorNodeId,
            initiator.DisplayName,
            SwarmPeerRole.Seeder,
            totalChunks,
            preset.TotalBytes));

        // Add initial Downloader participants
        foreach (var r in recipients)
        {
            swarmState.Participants.Add(new ActiveSwarmParticipant(
                r.Id,
                r.DisplayName,
                SwarmPeerRole.Leecher,
                totalChunks,
                preset.TotalBytes));
        }

        _activeSwarms[sessionId] = swarmState;

        // Register swarm in world state and pin to workbench
        _worldState.RegisterSwarm(swarmSummary);
        _worldState.TogglePinCard(swarmSummary);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            initiator.DisplayName,
            initiatorNodeId,
            "SwarmFileTransfer",
            $"Initiated '{presetSize}' file transfer '{preset.RelativeFileName}' ({preset.TotalBytes:N0} bytes, {totalChunks} chunks) with SHA-256 {preset.Sha256Hex}."));

        // Dispatch offers to recipients without cross-host RAM peeking (Anti-Shortcut Rule 2)
        foreach (var r in recipients)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                r.Id,
                SignalCategory.FileTransferOfferReceived,
                Details: sessionId.ToString(),
                Payload: new FileTransferOfferSignalPayload(sessionId, initiator.PublicIdentityId, preset.RelativeFileName, preset.TotalBytes, totalChunks)));

            if (r.AutoAcceptFileTransfers)
            {
                var capturedMember = r;
                var capturedSessionId = sessionId;
                UiTaskRunner.FireAndForget(
                    () => AcceptTransferAsync(capturedMember.Id, capturedSessionId),
                    "AutoAcceptFileTransfer",
                    _logSink);
            }
        }

        _flightRecorder.RecordStep(new InitiateFileTransferStep(
            initiator.DisplayName,
            recipients.Select(r => r.DisplayName).ToList(),
            presetSize));

        return sessionId;
    }

    public async Task<bool> AcceptTransferAsync(SimulatedNodeId recipientNodeId, Guid sessionId, CancellationToken ct = default)
    {
        if (!_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            return false;
        }

        var recipient = _worldState.GetPersonaHost(recipientNodeId);
        if (recipient == null) return false;

        recipient.AllowFileTransfers = true;
        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            recipient.DisplayName,
            recipient.Id,
            "PrivacyPolicy",
            FileTransferIngressFilter.PrivacyNotice));

        using (var recipScope = recipient.Services.CreateScope())
        {
            var actionHandler = recipScope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var sessionRepo = recipScope.ServiceProvider.GetRequiredService<ITransferSessionRepository>();
            var transferSessionId = TransferSessionId.FromGuid(sessionId);
            var existingSession = await sessionRepo.GetSessionByIdAsync(transferSessionId, ct);
            if (existingSession != null)
            {
                var destinationRoot = RootDirectoryPath.Parse(recipient.NodeDownloadsDirectory);
                var acceptResult = await actionHandler.AcceptTransferAsync(transferSessionId, destinationRoot, ct: ct);
                if (acceptResult.IsSuccess)
                {
                    await recipient.FlushOutboxAsync(ct);
                }
                else
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Warning,
                        recipient.DisplayName,
                        recipient.Id,
                        "SwarmFileTransfer",
                        $"In-band transfer acceptance warning: {acceptResult.Error!.Description}"));
                }
            }
        }

        var participant = swarm.Participants.FirstOrDefault(p => p.NodeId == recipientNodeId);
        if (participant == null)
        {
            participant = new ActiveSwarmParticipant(recipientNodeId, recipient.DisplayName, SwarmPeerRole.Leecher, swarm.TotalChunks, swarm.Preset.TotalBytes);
            lock (swarm.Participants)
            {
                swarm.Participants.Add(participant);
            }
        }

        string destPath = Path.Combine(recipient.NodeDownloadsDirectory, swarm.Preset.RelativeFileName);
        Directory.CreateDirectory(recipient.NodeDownloadsDirectory);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            recipient.DisplayName,
            recipientNodeId,
            "SwarmFileTransfer",
            $"Accepted file transfer '{swarm.Preset.RelativeFileName}'. Beginning multi-stream download into '{destPath}'."));

        _flightRecorder.RecordStep(new AcceptFileTransferStep(recipient.DisplayName));

        // Start background chunk download loop over real loopback TCP sockets
        _ = Task.Run(async () =>
        {
            await RunDownloaderLoopAsync(swarm, participant, recipient, destPath, swarm.Cts.Token);
        });

        return true;
    }

    public Task<bool> RejectTransferAsync(SimulatedNodeId nodeId, Guid sessionId)
    {
        if (_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            var participant = swarm.Participants.FirstOrDefault(p => p.NodeId == nodeId);
            if (participant != null)
            {
                participant.IsCancelled = true;
                participant.PauseGate.Set();
            }
            var host = _worldState.GetPersonaHost(nodeId);
            var name = host?.DisplayName ?? nodeId.ToString()[..8];
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                name,
                nodeId,
                "SwarmFileTransfer",
                $"Declined file transfer '{swarm.Preset.RelativeFileName}'."));
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public async Task<bool> InjectLateDownloaderAsync(Guid sessionId, SimulatedNodeId lateJoinerId, double progressThreshold = 0.5)
    {
        if (!_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            return false;
        }

        var lateJoiner = _worldState.GetPersonaHost(lateJoinerId);
        if (lateJoiner == null) return false;

        // Check if already in participants
        if (swarm.Participants.Any(p => p.NodeId == lateJoinerId))
        {
            return false;
        }

        swarm.PendingLateJoinerId = lateJoinerId;
        swarm.LateJoinerThreshold = progressThreshold;

        // Check if primary download is already past threshold
        var primaryDownloader = swarm.Participants.FirstOrDefault(p => p.Role == SwarmPeerRole.Leecher);
        double currentProgress = primaryDownloader != null && primaryDownloader.TotalChunks > 0
            ? (double)primaryDownloader.CompletedChunks / primaryDownloader.TotalChunks
            : 0;

        if (currentProgress >= progressThreshold && !swarm.LateJoinerInjected)
        {
            await TriggerLateJoinerAsync(swarm, lateJoiner);
        }
        else
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                "SwarmCoordinator",
                null,
                "LateJoiner",
                $"Scheduled late downloader '{lateJoiner.DisplayName}' to inject into swarm when download passes {progressThreshold:P0}."));
        }

        _flightRecorder.RecordStep(new InjectLateDownloaderStep(lateJoiner.DisplayName, progressThreshold));
        return true;
    }

    private async Task TriggerLateJoinerAsync(ActiveSwarmState swarm, ISimulatedPersonaHost lateJoiner)
    {
        lock (swarm)
        {
            if (swarm.LateJoinerInjected) return;
            swarm.LateJoinerInjected = true;
        }

        lateJoiner.AllowFileTransfers = true;
        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            lateJoiner.DisplayName,
            lateJoiner.Id,
            "PrivacyPolicy",
            FileTransferIngressFilter.PrivacyNotice));

        var participant = new ActiveSwarmParticipant(
            lateJoiner.Id,
            lateJoiner.DisplayName,
            SwarmPeerRole.Leecher,
            swarm.TotalChunks,
            swarm.Preset.TotalBytes);

        lock (swarm.Participants)
        {
            swarm.Participants.Add(participant);
        }

        string destPath = Path.Combine(lateJoiner.NodeDownloadsDirectory, swarm.Preset.RelativeFileName);
        Directory.CreateDirectory(lateJoiner.NodeDownloadsDirectory);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            lateJoiner.DisplayName,
            lateJoiner.Id,
            "SwarmFileTransfer",
            $"Late downloader '{lateJoiner.DisplayName}' injected into swarm! Downloading concurrently from seeder '{swarm.InitiatorName}' and partial peers."));

        _ = Task.Run(async () =>
        {
            await RunDownloaderLoopAsync(swarm, participant, lateJoiner, destPath, swarm.Cts.Token, isLateJoiner: true);
        });

        await Task.CompletedTask;
    }

    public Task<bool> PauseTransferAsync(Guid sessionId)
    {
        if (_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            foreach (var p in swarm.Participants)
            {
                if (p.Role == SwarmPeerRole.Leecher)
                {
                    p.IsPaused = true;
                    p.PauseGate.Reset();
                }
            }
            _flightRecorder.RecordStep(new PauseFileTransferStep(swarm.InitiatorName));
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                "SwarmCoordinator",
                null,
                "SwarmFileTransfer",
                $"Paused transfer session {sessionId}."));
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> ResumeTransferAsync(Guid sessionId)
    {
        if (_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            foreach (var p in swarm.Participants)
            {
                if (p.Role == SwarmPeerRole.Leecher)
                {
                    p.IsPaused = false;
                    p.PauseGate.Set();
                }
            }
            _flightRecorder.RecordStep(new ResumeFileTransferStep(swarm.InitiatorName));
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                "SwarmCoordinator",
                null,
                "SwarmFileTransfer",
                $"Resumed transfer session {sessionId}."));
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> CancelTransferAsync(Guid sessionId)
    {
        if (_activeSwarms.TryGetValue(sessionId, out var swarm))
        {
            swarm.Cts.Cancel();
            foreach (var p in swarm.Participants)
            {
                p.IsCancelled = true;
                p.PauseGate.Set();
            }
            _flightRecorder.RecordStep(new CancelFileTransferStep(swarm.InitiatorName));
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                "SwarmCoordinator",
                null,
                "SwarmFileTransfer",
                $"Cancelled transfer session {sessionId}."));
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task RunDownloaderLoopAsync(
        ActiveSwarmState swarm,
        ActiveSwarmParticipant participant,
        ISimulatedPersonaHost receiver,
        string destinationPath,
        CancellationToken ct,
        bool isLateJoiner = false)
    {
        try
        {
            // Pre-allocate destination file on disk
            await using (var preAlloc = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                preAlloc.SetLength(swarm.Preset.TotalBytes);
            }

            // Real loopback TCP socket connection between Seeder and Downloader
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int transferPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, transferPort, ct);
            var serverSocket = await listener.AcceptSocketAsync(ct);
            await connectTask;
            listener.Stop();

            await using var senderStream = new TcpTransferStream(StreamIndex.FromInt(0), new NetworkStream(serverSocket, ownsSocket: true), serverSocket);
            await using var receiverStream = new TcpTransferStream(StreamIndex.FromInt(0), new NetworkStream(clientSocket, ownsSocket: true), clientSocket);

            var key = TransferKey.Generate();
            var baseNonce = BaseNonce.Generate();

            await using var destFs = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            byte[] plaintextBuffer = new byte[swarm.ChunkSize];
            var startTime = DateTime.UtcNow;

            for (int chunkIndex = 0; chunkIndex < swarm.TotalChunks; chunkIndex++)
            {
                if (ct.IsCancellationRequested || participant.IsCancelled) break;

                // Deterministic non-polling pause await (Rule 15)
                await participant.PauseGate.WaitAsync(ct);

                if (ct.IsCancellationRequested || participant.IsCancelled) break;

                // 1. Mark in-flight
                participant.Pieces[chunkIndex] = PieceState.InFlight;
                _stateBus.Publish(new SimulatorStateSignal(
                    participant.NodeId,
                    SignalCategory.SwarmProgressChanged,
                    Payload: new SwarmChunkProgressSignal(
                        TransferSessionId.FromGuid(swarm.SessionId),
                        receiver.PublicIdentityId,
                        chunkIndex,
                        PieceState.InFlight,
                        participant.CompletedChunks,
                        swarm.TotalChunks,
                        participant.DownloadedBytes,
                        swarm.Preset.TotalBytes)));

                // 2. Downloader sends RequestChunkFrame over loopback TCP socket
                await receiverStream.SendFrameAsync(new RequestChunkFrame(ChunkIndex.FromInt(chunkIndex)), ct);

                // 3. Seeder/Peer receives RequestChunkFrame from TCP socket
                var reqFrame = await senderStream.ReceiveFrameAsync(ct);

                // 4. Multi-Source Swarm Peer Distribution:
                // If another peer has already verified this piece on disk, download from that peer!
                // Otherwise read from original seeder file.
                string sourceFilePath = swarm.Preset.FilePath;
                string sourceDisplayName = swarm.InitiatorName;
                SimulatedNodeId sourceNodeId = swarm.InitiatorId;

                if (isLateJoiner)
                {
                    var peerWithChunk = swarm.Participants.FirstOrDefault(p =>
                        p.NodeId != participant.NodeId &&
                        p.Role == SwarmPeerRole.Leecher &&
                        p.Pieces.Length > chunkIndex &&
                        p.Pieces[chunkIndex] == PieceState.Verified);

                    if (peerWithChunk != null)
                    {
                        var peerHost = _worldState.GetPersonaHost(peerWithChunk.NodeId);
                        if (peerHost != null)
                        {
                            string candidatePath = Path.Combine(peerHost.NodeDownloadsDirectory, swarm.Preset.RelativeFileName);
                            if (File.Exists(candidatePath))
                            {
                                sourceFilePath = candidatePath;
                                sourceDisplayName = peerWithChunk.DisplayName;
                                sourceNodeId = peerWithChunk.NodeId;
                            }
                        }
                    }
                }

                long offset = (long)chunkIndex * swarm.ChunkSize;
                int expectedLength = (int)Math.Min(swarm.ChunkSize, swarm.Preset.TotalBytes - offset);

                int bytesRead;
                await using (var sourceFs = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    sourceFs.Seek(offset, SeekOrigin.Begin);
                    bytesRead = await sourceFs.ReadAsync(plaintextBuffer.AsMemory(0, expectedLength), ct);
                }

                // Log multi-source peer transfer sample
                if (isLateJoiner && sourceNodeId != swarm.InitiatorId && (chunkIndex % 4 == 0 || chunkIndex == 0))
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Information,
                        participant.DisplayName,
                        participant.NodeId,
                        "SwarmFileTransfer",
                        $"[Swarm P2P Piece] Transferred chunk {chunkIndex} directly from peer '{sourceDisplayName}' (multi-source swarm)."));
                }

                // 5. Encrypt chunk with AES-GCM
                byte[] ciphertextWithTag = new byte[bytesRead + ChunkCryptoService.TagSizeBytes];
                ChunkCryptoService.EncryptChunkInto(
                    plaintextBuffer.AsSpan(0, bytesRead),
                    key,
                    baseNonce,
                    ChunkIndex.FromInt(chunkIndex),
                    ciphertextWithTag.AsSpan());

                // 6. Seeder/Peer sends ChunkDataFrame over TCP socket
                await senderStream.SendFrameAsync(new ChunkDataFrame(
                    ChunkIndex.FromInt(chunkIndex),
                    EncryptedChunkData.FromBytes(ciphertextWithTag)), ct);

                // 7. Downloader receives ChunkDataFrame from TCP socket
                var dataFrame = (ChunkDataFrame)await receiverStream.ReceiveFrameAsync(ct);

                // 8. Downloader decrypts and verifies SHA-256 Merkle leaf hash
                byte[] decrypted = new byte[bytesRead];
                ChunkCryptoService.DecryptChunkInto(
                    dataFrame.Data.CiphertextWithAuthTag.Span,
                    key,
                    baseNonce,
                    ChunkIndex.FromInt(chunkIndex),
                    decrypted.AsSpan());

                ChunkHash computedLeaf = MerkleTreeBuilder.HashLeaf(decrypted.AsSpan(0, bytesRead));
                ChunkHash expectedLeaf = swarm.LeafHashes[chunkIndex];

                if (!computedLeaf.Span.SequenceEqual(expectedLeaf.Span))
                {
                    participant.Pieces[chunkIndex] = PieceState.Corrupted;
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Error,
                        participant.DisplayName,
                        participant.NodeId,
                        "SwarmFileTransfer",
                        $"Merkle proof failed for chunk {chunkIndex}!"));
                    break;
                }

                // 9. Write verified chunk to disk
                destFs.Seek(offset, SeekOrigin.Begin);
                await destFs.WriteAsync(decrypted.AsMemory(0, bytesRead), ct);
                await destFs.FlushAsync(ct);

                // 10. Downloader sends HaveFrame back over TCP socket
                await receiverStream.SendFrameAsync(new HaveFrame(ChunkIndex.FromInt(chunkIndex)), ct);
                var haveFrame = await senderStream.ReceiveFrameAsync(ct);

                // 11. Update metrics & progress
                participant.Pieces[chunkIndex] = PieceState.Verified;
                participant.CompletedChunks++;
                participant.DownloadedBytes += bytesRead;

                double elapsedSec = Math.Max(0.05, (DateTime.UtcNow - startTime).TotalSeconds);
                participant.SpeedBytesPerSec = participant.DownloadedBytes / elapsedSec;

                _stateBus.Publish(new SimulatorStateSignal(
                    participant.NodeId,
                    SignalCategory.SwarmProgressChanged,
                    Payload: new SwarmChunkProgressSignal(
                        TransferSessionId.FromGuid(swarm.SessionId),
                        receiver.PublicIdentityId,
                        chunkIndex,
                        PieceState.Verified,
                        participant.CompletedChunks,
                        swarm.TotalChunks,
                        participant.DownloadedBytes,
                        swarm.Preset.TotalBytes,
                        participant.SpeedBytesPerSec)));

                // Check 50% late joiner trigger
                if (!isLateJoiner && swarm.PendingLateJoinerId.HasValue && !swarm.LateJoinerInjected)
                {
                    double progress = (double)participant.CompletedChunks / swarm.TotalChunks;
                    if (progress >= swarm.LateJoinerThreshold)
                    {
                        var lateHost = _worldState.GetPersonaHost(swarm.PendingLateJoinerId.Value);
                        if (lateHost != null)
                        {
                            _ = Task.Run(async () => await TriggerLateJoinerAsync(swarm, lateHost));
                        }
                    }
                }
            }

            destFs.Close();

            if (participant.CompletedChunks == swarm.TotalChunks)
            {
                participant.IsComplete = true;

                // 12. Complete disk integrity check: hash final file on disk
                string diskSha256;
                using (var verifyStream = File.OpenRead(destinationPath))
                {
                    byte[] hashBytes = await SHA256.HashDataAsync(verifyStream, ct);
                    diskSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                if (diskSha256 == swarm.Preset.Sha256Hex)
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Information,
                        participant.DisplayName,
                        participant.NodeId,
                        "SwarmFileTransfer",
                        $"File transfer completed & verified: '{swarm.Preset.RelativeFileName}' ({swarm.Preset.TotalBytes:N0} bytes, {swarm.TotalChunks} chunks) with SHA-256 {diskSha256} saved to '{destinationPath}'."));

                    _stateBus.Publish(new SimulatorStateSignal(
                        participant.NodeId,
                        SignalCategory.FileTransferCompleted,
                        Details: diskSha256,
                        Payload: new FileTransferCompletedSignalPayload(swarm.SessionId, receiver.PublicIdentityId, destinationPath, diskSha256)));
                }
                else
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Error,
                        participant.DisplayName,
                        participant.NodeId,
                        "SwarmFileTransfer",
                        $"Disk integrity check failed! Expected {swarm.Preset.Sha256Hex}, got {diskSha256}."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean cancellation
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                participant.DisplayName,
                participant.NodeId,
                "SwarmFileTransfer",
                $"Transfer error: {ex.Message}"));
        }
    }
}
