namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;

public interface IUdpStunEndpointProvider
{
    IPEndPoint? BoundEndpoint { get; }
    bool IsRunning { get; }
}

public sealed class UdpStunResponderOptions
{
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = 0;
    public int MaxRequestsPerSecondPerIp { get; set; } = 60;
    public int MaxTrackedRateLimitIps { get; set; } = 4096;
}

/// <summary>
/// Pure stateless RFC 5389 STUN Binding Request / XOR-MAPPED-ADDRESS codec.
/// </summary>
public static class Rfc5389StunCodec
{
    public const int HeaderSizeBytes = 20;
    public const int MaxRequestSizeBytes = 548;
    public const int MaxResponseSizeBytes = 44;
    public const ushort BindingRequestType = 0x0001;
    public const ushort BindingSuccessResponseType = 0x0101;
    public const uint MagicCookie = 0x2112A442;
    public const ushort XorMappedAddressAttributeType = 0x0020;

    public static void BuildBindingRequest(ReadOnlySpan<byte> transactionId, Span<byte> destination)
    {
        if (transactionId.Length != 12)
        {
            throw new ArgumentException("STUN transaction ID must be exactly 12 bytes.", nameof(transactionId));
        }

        if (destination.Length < HeaderSizeBytes)
        {
            throw new ArgumentException("Destination buffer must be at least 20 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), BindingRequestType);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), MagicCookie);
        transactionId.CopyTo(destination.Slice(8, 12));
    }

    public static bool TryBuildBindingResponse(
        ReadOnlySpan<byte> request,
        IPEndPoint remoteEndPoint,
        Span<byte> responseBuffer,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (remoteEndPoint == null || request.Length < HeaderSizeBytes || request.Length > MaxRequestSizeBytes)
        {
            return false;
        }

        ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(0, 2));
        if ((messageType & 0xC000) != 0 || messageType != BindingRequestType)
        {
            return false;
        }

        ushort messageLength = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(2, 2));
        if ((messageLength & 0x0003) != 0 || HeaderSizeBytes + messageLength != request.Length)
        {
            return false;
        }

        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(request.Slice(4, 4));
        if (cookie != MagicCookie)
        {
            return false;
        }

        var address = remoteEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        bool isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (!isIpv4 && !isIpv6)
        {
            return false;
        }

        ushort attrValueLen = isIpv4 ? (ushort)8 : (ushort)20;
        ushort attrTotalLen = (ushort)(4 + attrValueLen);
        int totalLen = HeaderSizeBytes + attrTotalLen;
        if (responseBuffer.Length < totalLen)
        {
            return false;
        }

        // 20-byte RFC 5389 Header
        BinaryPrimitives.WriteUInt16BigEndian(responseBuffer.Slice(0, 2), BindingSuccessResponseType);
        BinaryPrimitives.WriteUInt16BigEndian(responseBuffer.Slice(2, 2), attrTotalLen);
        BinaryPrimitives.WriteUInt32BigEndian(responseBuffer.Slice(4, 4), MagicCookie);
        request.Slice(8, 12).CopyTo(responseBuffer.Slice(8, 12));

        // XOR-MAPPED-ADDRESS Attribute (0x0020)
        BinaryPrimitives.WriteUInt16BigEndian(responseBuffer.Slice(20, 2), XorMappedAddressAttributeType);
        BinaryPrimitives.WriteUInt16BigEndian(responseBuffer.Slice(22, 2), attrValueLen);
        responseBuffer[24] = 0x00; // Reserved
        responseBuffer[25] = isIpv4 ? (byte)0x01 : (byte)0x02;

        ushort xPort = (ushort)((ushort)remoteEndPoint.Port ^ (ushort)(MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(responseBuffer.Slice(26, 2), xPort);

        Span<byte> addrBytes = stackalloc byte[16];
        if (!address.TryWriteBytes(addrBytes, out int addrWritten))
        {
            return false;
        }

        if (isIpv4 && addrWritten == 4)
        {
            ReadOnlySpan<byte> cookieBytes = responseBuffer.Slice(4, 4);
            for (int i = 0; i < 4; i++)
            {
                responseBuffer[28 + i] = (byte)(addrBytes[i] ^ cookieBytes[i]);
            }
        }
        else if (isIpv6 && addrWritten == 16)
        {
            ReadOnlySpan<byte> mask = responseBuffer.Slice(4, 16); // MagicCookie (4) || TransactionId (12)
            for (int i = 0; i < 16; i++)
            {
                responseBuffer[28 + i] = (byte)(addrBytes[i] ^ mask[i]);
            }
        }
        else
        {
            return false;
        }

        bytesWritten = totalLen;
        return true;
    }

    public static bool TryParseBindingResponse(
        ReadOnlySpan<byte> response,
        ReadOnlySpan<byte> expectedTransactionId,
        out IPEndPoint? mappedEndPoint)
    {
        mappedEndPoint = null;
        if (expectedTransactionId.Length != 12 || response.Length < HeaderSizeBytes)
        {
            return false;
        }

        ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(0, 2));
        ushort msgLen = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(4, 4));
        if (msgType != BindingSuccessResponseType || cookie != MagicCookie || HeaderSizeBytes + msgLen > response.Length)
        {
            return false;
        }

        if (!response.Slice(8, 12).SequenceEqual(expectedTransactionId))
        {
            return false;
        }

        int offset = HeaderSizeBytes;
        int end = HeaderSizeBytes + msgLen;
        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            offset += 4;

            if (offset + attrLen > end)
            {
                return false;
            }

            if (attrType == XorMappedAddressAttributeType && attrLen >= 8)
            {
                byte family = response[offset + 1];
                ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
                ushort port = (ushort)(xPort ^ (ushort)(MagicCookie >> 16));

                if (family == 0x01 && attrLen == 8)
                {
                    Span<byte> ip = stackalloc byte[4];
                    ReadOnlySpan<byte> cookieBytes = response.Slice(4, 4);
                    for (int i = 0; i < 4; i++)
                    {
                        ip[i] = (byte)(response[offset + 4 + i] ^ cookieBytes[i]);
                    }

                    mappedEndPoint = new IPEndPoint(new IPAddress(ip), port);
                    return true;
                }

                if (family == 0x02 && attrLen == 20)
                {
                    Span<byte> ip = stackalloc byte[16];
                    ReadOnlySpan<byte> mask = response.Slice(4, 16);
                    for (int i = 0; i < 16; i++)
                    {
                        ip[i] = (byte)(response[offset + 4 + i] ^ mask[i]);
                    }

                    mappedEndPoint = new IPEndPoint(new IPAddress(ip), port);
                    return true;
                }
            }

            // Attributes are padded to 4-byte boundaries in RFC 5389
            int paddedLen = (attrLen + 3) & ~3;
            offset += paddedLen;
        }

        return false;
    }
}

/// <summary>
/// Lightweight stateless embedded RFC 5389 UDP STUN Binding responder.
/// Binds only when RelayFeatureFlags.SwarmTracker is enabled; zero disk, zero authentication,
/// bounded packet size and per-IP rate limiting.
/// </summary>
public sealed class UdpStunResponderService : IUdpStunEndpointProvider, IHostedService, IAsyncDisposable
{
    private readonly IRelayHostingPolicyProvider _policyProvider;
    private readonly UdpStunResponderOptions _options;
    private readonly IDateTimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, (long WindowSecond, int Count)> _rateLimits = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveLoopTask;
    private bool _hostStarted;
    private bool _disposed;

    public IPEndPoint? BoundEndpoint
    {
        get
        {
            lock (_sync)
            {
                return _udpClient?.Client.LocalEndPoint as IPEndPoint;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _udpClient != null;
            }
        }
    }

    public UdpStunResponderService(
        IRelayHostingPolicyProvider policyProvider,
        UdpStunResponderOptions options,
        IDateTimeProvider clock)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        if (_policyProvider is RelayHostingPolicyHolder holder)
        {
            holder.PolicyChanged += OnPolicyChanged;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostStarted = true;
            if (_policyProvider.GetPolicy().IsFeatureEnabled(RelayFeatureFlags.SwarmTracker))
            {
                StartListenerLocked();
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loopTask;
        lock (_sync)
        {
            _hostStarted = false;
            loopTask = StopListenerLocked();
        }

        if (loopTask != null)
        {
            try
            {
                await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void OnPolicyChanged(RelayHostingPolicy policy)
    {
        lock (_sync)
        {
            if (_disposed || !_hostStarted)
            {
                return;
            }

            if (policy.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker))
            {
                StartListenerLocked();
            }
            else
            {
                _ = StopListenerLocked();
            }
        }
    }

    private void StartListenerLocked()
    {
        if (_udpClient != null)
        {
            return;
        }

        var bindAddress = _options.BindAddress ?? IPAddress.Loopback;
        var client = new UdpClient(new IPEndPoint(bindAddress, _options.Port));
        var cts = new CancellationTokenSource();
        _udpClient = client;
        _loopCts = cts;
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(client, cts.Token));
    }

    private Task? StopListenerLocked()
    {
        var client = _udpClient;
        var cts = _loopCts;
        var loopTask = _receiveLoopTask;
        _udpClient = null;
        _loopCts = null;
        _receiveLoopTask = null;

        try { cts?.Cancel(); } catch { }
        try { client?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }

        return loopTask;
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        byte[] responseBuffer = new byte[Rfc5389StunCodec.MaxResponseSizeBytes];

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (!_policyProvider.GetPolicy().IsFeatureEnabled(RelayFeatureFlags.SwarmTracker))
            {
                continue;
            }

            if (!TryConsumeRateLimit(received.RemoteEndPoint.Address))
            {
                continue;
            }

            if (Rfc5389StunCodec.TryBuildBindingResponse(
                received.Buffer,
                received.RemoteEndPoint,
                responseBuffer,
                out int bytesWritten))
            {
                try
                {
                    await client.SendAsync(responseBuffer.AsMemory(0, bytesWritten), received.RemoteEndPoint, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private bool TryConsumeRateLimit(IPAddress sourceIp)
    {
        long currentSecond = _clock.UtcNow.ToUnixTimeSeconds();
        lock (_rateLimits)
        {
            if (_rateLimits.Count >= _options.MaxTrackedRateLimitIps && !_rateLimits.ContainsKey(sourceIp))
            {
                _rateLimits.Clear();
            }

            if (_rateLimits.TryGetValue(sourceIp, out var state) && state.WindowSecond == currentSecond)
            {
                if (state.Count >= _options.MaxRequestsPerSecondPerIp)
                {
                    return false;
                }

                _rateLimits[sourceIp] = (currentSecond, state.Count + 1);
                return true;
            }

            _rateLimits[sourceIp] = (currentSecond, 1);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loopTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hostStarted = false;
            if (_policyProvider is RelayHostingPolicyHolder holder)
            {
                holder.PolicyChanged -= OnPolicyChanged;
            }

            loopTask = StopListenerLocked();
        }

        if (loopTask != null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
