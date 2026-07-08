using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Signal.Interop;

namespace Percolator.Infrastructure.Cryptography;

public sealed class SenderKeyCryptographyService : ISenderKeyCryptographyService, IDisposable
{
    private readonly ISenderKeyInteropBridge _storeBridge;
    private readonly ILogger<SenderKeyCryptographyService> _logger;
    private readonly IntPtr _vtablePtr;

    [StructLayout(LayoutKind.Sequential)]
    private struct SenderKeyStoreVTable
    {
        public IntPtr LoadSenderKey;
        public IntPtr StoreSenderKey;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int LoadSenderKeyDelegate(
        IntPtr senderAddress,
        byte* distributionIdBytes,
        UIntPtr distributionIdLen,
        out IntPtr outRecord,
        out UIntPtr outLen
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int StoreSenderKeyDelegate(
        IntPtr senderAddress,
        byte* distributionIdBytes,
        UIntPtr distributionIdLen,
        byte* recordBytes,
        UIntPtr recordLen
    );

    private LoadSenderKeyDelegate _loadDelegate;
    private StoreSenderKeyDelegate _storeDelegate;
    
    private GCHandle _loadDelegateHandle;
    private GCHandle _storeDelegateHandle;
    private GCHandle _vTableHandle;
    private bool _disposed;

    public unsafe SenderKeyCryptographyService(ISenderKeyInteropBridge storeBridge, ILogger<SenderKeyCryptographyService> logger)
    {
        _storeBridge = storeBridge;
        _logger = logger;
        
        // Setup VTable
        _loadDelegate = LoadKey;
        _storeDelegate = StoreKey;
        
        _loadDelegateHandle = GCHandle.Alloc(_loadDelegate);
        _storeDelegateHandle = GCHandle.Alloc(_storeDelegate);
        
        var vTable = new SenderKeyStoreVTable
        {
            LoadSenderKey = Marshal.GetFunctionPointerForDelegate(_loadDelegate),
            StoreSenderKey = Marshal.GetFunctionPointerForDelegate(_storeDelegate)
        };
        
        _vTableHandle = GCHandle.Alloc(vTable, GCHandleType.Pinned);
        _vtablePtr = _vTableHandle.AddrOfPinnedObject();
    }

    private unsafe int LoadKey(IntPtr senderAddress, byte* distributionIdBytes, UIntPtr distributionIdLen, out IntPtr outRecord, out UIntPtr outLen)
    {
        outRecord = IntPtr.Zero;
        outLen = UIntPtr.Zero;
        
        try
        {
            // DistributionID should be 16 bytes for Guid
            if (distributionIdLen.ToUInt32() != 16) return 1; // Error
            
            var distributionIdSpan = new ReadOnlySpan<byte>(distributionIdBytes, 16);
            var conversationId = new Percolator.Cryptography.Primitives.ConversationId(new Guid(distributionIdSpan));
            
            var uuid = SignalCrypto.GetSenderAddressName(senderAddress);
            var deviceId = SignalCrypto.GetSenderAddressDeviceId(senderAddress);
            
            // Use the UUID directly as CryptoPublicIdentity
            var publicIdentityId = new CryptoPublicIdentity(uuid);
            var devId = new DeviceId(deviceId);

            if (_storeBridge.TryLoadSenderKey(conversationId, publicIdentityId, devId, out var recordBytes))
            {
                var safeHandle = SignalCrypto.DeserializeSenderKeyRecord(recordBytes);
                outRecord = safeHandle.DangerousGetHandle();
                safeHandle.SetHandleAsInvalid();
                return 0; // Success
            }
            
            return 0; // Success, but not found (outRecord is null/Zero)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sender key");
            return 2; // Error
        }
    }

    private unsafe int StoreKey(IntPtr senderAddress, byte* distributionIdBytes, UIntPtr distributionIdLen, byte* recordBytes, UIntPtr recordLen)
    {
        try
        {
            if (distributionIdLen.ToUInt32() != 16) return 1;
            
            var distributionIdSpan = new ReadOnlySpan<byte>(distributionIdBytes, 16);
            var conversationId = new Percolator.Cryptography.Primitives.ConversationId(new Guid(distributionIdSpan));
            
            var uuid = SignalCrypto.GetSenderAddressName(senderAddress);
            var deviceId = SignalCrypto.GetSenderAddressDeviceId(senderAddress);
            
            // Use the UUID directly as CryptoPublicIdentity
            var publicIdentityId = new CryptoPublicIdentity(uuid);
            var devId = new DeviceId(deviceId);
            
            var recordSpan = new ReadOnlySpan<byte>(recordBytes, (int)recordLen.ToUInt32());
            
            // Store using bridge
            _storeBridge.StoreSenderKey(conversationId, publicIdentityId, devId, recordSpan.ToArray());
            
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store sender key");
            return 2;
        }
    }

    public SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(
        Percolator.Cryptography.Primitives.ConversationId conversationId, 
        CryptoPublicIdentity publicIdentityId, 
        DeviceId deviceId)
    {
        var uuidBytes = publicIdentityId.Value.ToByteArray();
        using var addressHandle = SignalCrypto.NewSenderAddress(uuidBytes, deviceId.Value);
        using var messageHandle = SignalCrypto.CreateSenderKeyDistributionMessage(_vtablePtr, addressHandle, conversationId.Value);

        var bytes = SignalCrypto.SerializeSenderKeyDistributionMessage(messageHandle);
        return SenderKeyDistributionMessageBytes.FromBytesOwned(bytes);
    }

    public void ProcessSenderKeyDistributionMessage(
        Percolator.Cryptography.Primitives.ConversationId conversationId,
        CryptoPublicIdentity senderPublicIdentityId,
        DeviceId senderDeviceId,
        SenderKeyDistributionMessageBytes distributionMessage)
    {
        var uuidBytes = senderPublicIdentityId.Value.ToByteArray();
        using var addressHandle = SignalCrypto.NewSenderAddress(uuidBytes, senderDeviceId.Value);
        using var messageHandle = SignalCrypto.DeserializeSenderKeyDistributionMessage(distributionMessage.Span);
        
        SignalCrypto.ProcessSenderKeyDistributionMessage(_vtablePtr, addressHandle, messageHandle);
    }

    public byte[] EncryptGroupMessage(
        Percolator.Cryptography.Primitives.ConversationId conversationId,
        CryptoPublicIdentity publicIdentityId,
        DeviceId deviceId,
        ReadOnlySpan<byte> plaintext)
    {
        var uuidBytes = publicIdentityId.Value.ToByteArray();
        using var addressHandle = SignalCrypto.NewSenderAddress(uuidBytes, deviceId.Value);
        using var messageHandle = SignalCrypto.EncryptGroupMessage(_vtablePtr, addressHandle, conversationId.Value, plaintext);
        
        return SignalCrypto.SerializeSenderKeyMessage(messageHandle);
    }

    public byte[] DecryptGroupMessage(
        Percolator.Cryptography.Primitives.ConversationId conversationId,
        CryptoPublicIdentity senderPublicIdentityId,
        DeviceId senderDeviceId,
        ReadOnlySpan<byte> ciphertext)
    {
        var uuidBytes = senderPublicIdentityId.Value.ToByteArray();
        using var addressHandle = SignalCrypto.NewSenderAddress(uuidBytes, senderDeviceId.Value);
        using var messageHandle = SignalCrypto.DeserializeSenderKeyMessage(ciphertext);
        
        return SignalCrypto.DecryptGroupMessage(_vtablePtr, addressHandle, messageHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        if (_loadDelegateHandle.IsAllocated) _loadDelegateHandle.Free();
        if (_storeDelegateHandle.IsAllocated) _storeDelegateHandle.Free();
        if (_vTableHandle.IsAllocated) _vTableHandle.Free();
        
        _disposed = true;
    }
}
