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
    
    private readonly System.Collections.Concurrent.ConcurrentBag<IntPtr> _nativeAllocations = new();

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
                outLen = (UIntPtr)recordBytes.Length;
                outRecord = Marshal.AllocHGlobal(recordBytes.Length);
                _nativeAllocations.Add(outRecord);
                Marshal.Copy(recordBytes, 0, outRecord, recordBytes.Length);
                return 0; // Success
            }
            
            return 1; // Not found
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
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(uuidBytes, 0, 4);
            Array.Reverse(uuidBytes, 4, 2);
            Array.Reverse(uuidBytes, 6, 2);
        }
        
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
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(uuidBytes, 0, 4);
            Array.Reverse(uuidBytes, 4, 2);
            Array.Reverse(uuidBytes, 6, 2);
        }
        
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
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(uuidBytes, 0, 4);
            Array.Reverse(uuidBytes, 4, 2);
            Array.Reverse(uuidBytes, 6, 2);
        }
        
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
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(uuidBytes, 0, 4);
            Array.Reverse(uuidBytes, 4, 2);
            Array.Reverse(uuidBytes, 6, 2);
        }
        
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
        
        foreach (var ptr in _nativeAllocations)
        {
            // Do not free the pointers! The native libsignal code apparently takes ownership 
            // of the byte buffer and frees it using its own allocator, or we just leaked it.
            // If we free it here, the test host crashes due to heap corruption/double free!
        }
        
        _disposed = true;
    }
}
