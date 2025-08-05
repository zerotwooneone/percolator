using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using NUnit.Framework;
using Percolator.Infrastructure;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public class FileBasedDoubleRatchetSessionStoreTests
{
    private Mock<IOptions<StorageOptions>> _mockOptions;
    private Mock<ILogger<FileBasedDoubleRatchetSessionStore>> _mockLogger;
    private string _testPath;
    
    [SetUp]
    public void Setup()
    {
        // Set up the test directory in a temporary path
        _testPath = Path.Combine(Path.GetTempPath(), "PercolatorTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testPath);
        
        // Set up mocks
        _mockOptions = new Mock<IOptions<StorageOptions>>();
        _mockOptions.Setup(o => o.Value).Returns(new StorageOptions { Path = _testPath });
        
        _mockLogger = new Mock<ILogger<FileBasedDoubleRatchetSessionStore>>();
    }
    
    [Test]
    public async Task SessionState_SetAndGet_MaintainsSymmetry()
    {
        // Arrange
        var sessionStore = new FileBasedDoubleRatchetSessionStore(_mockOptions.Object, _mockLogger.Object);
        var sessionId = new SessionId(Guid.NewGuid());
        
        // Create a test session state with known values
        var originalState = CreateTestSessionState();
        
        // Calculate hash of original root key
        var originalRootKeyHash = originalState.RootKey.Value != null 
            ? Convert.ToBase64String(SHA256.HashData(originalState.RootKey.Value))
            : "null";
        
        // Act
        await sessionStore.SetSessionStateAsync(sessionId, originalState);
        var retrievedState = await sessionStore.GetSessionStateAsync(sessionId);
        
        // Assert
        Assert.That(retrievedState, Is.Not.Null);
        
        // Verify root key is preserved
        var retrievedRootKeyHash = retrievedState.RootKey.Value != null 
            ? Convert.ToBase64String(SHA256.HashData(retrievedState.RootKey.Value))
            : "null";
        Assert.That(retrievedRootKeyHash, Is.EqualTo(originalRootKeyHash));
        Assert.That(retrievedState.RootKey.Value, Is.EqualTo(originalState.RootKey.Value));
        
        // Verify sending chain key
        VerifyChainKey(originalState.SendingChainKey, retrievedState.SendingChainKey);
        
        // Verify receiving chain key
        VerifyChainKey(originalState.ReceivingChainKey, retrievedState.ReceivingChainKey);
        
        // Verify counters
        Assert.That(retrievedState.SendingCounter, Is.EqualTo(originalState.SendingCounter));
        Assert.That(retrievedState.ReceivingCounter, Is.EqualTo(originalState.ReceivingCounter));
        
        // Verify skipped message keys
        Assert.That(retrievedState.SkippedMessageKeys.Count, Is.EqualTo(originalState.SkippedMessageKeys.Count));
        foreach (var key in originalState.SkippedMessageKeys.Keys)
        {
            Assert.That(retrievedState.SkippedMessageKeys.ContainsKey(key), Is.True);
            Assert.That(retrievedState.SkippedMessageKeys[key], Is.EqualTo(originalState.SkippedMessageKeys[key]));
        }
        
        // Verify identity public key
        Assert.That(retrievedState.TheirIdentityPublicKey.Value, Is.EqualTo(originalState.TheirIdentityPublicKey.Value));
        
        // Verify DH ratchet public key
        if (originalState.TheirDhRatchetPublicKey != null)
        {
            Assert.That(retrievedState.TheirDhRatchetPublicKey, Is.Not.Null);
            Assert.That(retrievedState.TheirDhRatchetPublicKey.Value, Is.EqualTo(originalState.TheirDhRatchetPublicKey.Value));
        }
        else
        {
            Assert.That(retrievedState.TheirDhRatchetPublicKey, Is.Null);
        }
        
        // Verify DH ratchet private key
        if (originalState.DhRatchetPrivateKey != null)
        {
            Assert.That(retrievedState.DhRatchetPrivateKey, Is.Not.Null);
            Assert.That(retrievedState.DhRatchetPrivateKey.Value, Is.EqualTo(originalState.DhRatchetPrivateKey.Value));
        }
        else
        {
            Assert.That(retrievedState.DhRatchetPrivateKey, Is.Null);
        }
    }
    
    private void VerifyChainKey(ChainKey originalChainKey, ChainKey retrievedChainKey)
    {
        if (originalChainKey == null)
        {
            Assert.That(retrievedChainKey, Is.Null);
            return;
        }
        
        Assert.That(retrievedChainKey, Is.Not.Null);
        Assert.That(retrievedChainKey.Value, Is.EqualTo(originalChainKey.Value));
    }
    
    private DoubleRatchetSession.DoubleRatchetSessionState CreateTestSessionState()
    {
        // Create random test data
        var rootKeyData = new byte[32];
        var sendingChainKeyData = new byte[32];
        var receivingChainKeyData = new byte[32];
        var identityPublicKeyData = new byte[32];
        var dhRatchetPublicKeyData = new byte[32];
        var dhRatchetPrivateKeyData = new byte[32];
        var messageKeyData = new byte[32];
        
        RandomNumberGenerator.Fill(rootKeyData);
        RandomNumberGenerator.Fill(sendingChainKeyData);
        RandomNumberGenerator.Fill(receivingChainKeyData);
        RandomNumberGenerator.Fill(identityPublicKeyData);
        RandomNumberGenerator.Fill(dhRatchetPublicKeyData);
        RandomNumberGenerator.Fill(dhRatchetPrivateKeyData);
        RandomNumberGenerator.Fill(messageKeyData);
        
        // Create the session state with all required properties
        return new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new RootKey(rootKeyData),
            SendingChainKey = new ChainKey(sendingChainKeyData),
            ReceivingChainKey = new ChainKey(receivingChainKeyData),
            SendingCounter = 1,
            ReceivingCounter = 2,
            SkippedMessageKeys = new Dictionary<SkippedMessageKeyIdentifier, byte[]>
            {
                { new SkippedMessageKeyIdentifier(new RatchetEphemeralKey(dhRatchetPublicKeyData), 5), messageKeyData }
            },
            TheirIdentityPublicKey = new RatchetIdentityKey(identityPublicKeyData),
            TheirDhRatchetPublicKey = new RatchetEphemeralKey(dhRatchetPublicKeyData),
            DhRatchetPrivateKey = new PrivateEphemeralKey(dhRatchetPrivateKeyData)
        };
    }
    
    [TearDown]
    public void TearDown()
    {
        // Clean up test directory if it exists
        if (Directory.Exists(_testPath))
        {
            try
            {
                Directory.Delete(_testPath, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
