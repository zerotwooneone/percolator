using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;
using CryptoRatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using CryptoRatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;
using CryptoPrivatePreKey = Percolator.Cryptography.PrivatePreKey;
using CryptoPrivateOneTimeKey = Percolator.Cryptography.PrivateOneTimeKey;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class SessionMessageTests
{
    
    
    private async Task<SessionRatchetMessage> EncryptViaStoreAsync(
        ActiveIdentityContext identity,
        SessionId sessionId,
        Plaintext plaintext)
    {
        if (identity.Identity is null) throw new InvalidOperationException("Identity context not loaded");
        throw new NotSupportedException("Cutover pending: store-backed encryption not implemented in tests");
    }

    private async Task<Plaintext> TryInferAndDecryptViaStoreAsync(
        ActiveIdentityContext identity,
        SessionRatchetMessage encrypted,
        CancellationToken ct)
    {
        if (identity.Identity is null) throw new InvalidOperationException("Identity context not loaded");
        throw new NotSupportedException("Cutover pending: store-backed decryption not implemented in tests");
    }
    private ActiveIdentityContext _aliceIdentity = null!;
    private ActiveIdentityContext _bobIdentity = null!;
    private ILoggerFactory _loggerFactory = null!;
    private IOptions<CryptographyOptions> _options = null!;
    private ECDiffieHellman _aliceEphemeral;

    [SetUp]
    public void SetUp()
    {
        // Create a logger factory with console and debug providers for diagnostics
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => 
            {
                // Use ConsoleFormatterOptions instead of the obsolete ConsoleLoggerOptions
                if (options.FormatterName == ConsoleFormatterNames.Simple)
                {
                    builder.AddSimpleConsole(formatterOptions => 
                    {
                        formatterOptions.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                    });
                }
            });
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        // Create cryptography options with diagnostic logging enabled
        _options = Options.Create(new CryptographyOptions { EnableCryptographicMaterialLogging = true });

        // Create identity contexts
        _aliceEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _aliceIdentity = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 1 },
            Keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                _aliceEphemeral
            )
        };

        _bobIdentity = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Bob") { SelfIdentityId = 1 },
            Keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };
    }

    [TearDown]
    public void TearDown()
    {
        // Properly dispose the logger factory
        _loggerFactory?.Dispose();
        _aliceEphemeral?.Dispose();
    }

    [Test]
    public async Task EncryptAndDecrypt_Should_SucceedSymmetrically_WhenSessionsAreEstablished()
    {
        // Log test information
        var logger = _loggerFactory.CreateLogger<SessionMessageTests>();
        logger.LogInformation("Starting EncryptAndDecrypt test with cryptographic diagnostic logging enabled");
        
        // Arrange: Establish a shared secret between Alice and Bob
        var (aliceSharedSecret, bobSharedSecret) = PerformX3DH();

        // Arrange: Use the shared secret to establish a double ratchet session
        var conversationId = new SessionId(Guid.NewGuid());
        var bobPeerId = new PeerId(_bobIdentity.Identity!.Id);
        var bobIdentityKey = new CryptoRatchetIdentityKey(_bobIdentity.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobRatchetKey = new RatchetEphemeralKey(_bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        

        var alicePeerId = new PeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new CryptoRatchetIdentityKey(_aliceIdentity.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
        var aliceEphemeralKey = new RatchetEphemeralKey(_aliceIdentity.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        // Create the ECDiffieHellman key using Bob's SignedPreKey that was used in the handshake
        using var bobHandshakeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        bobHandshakeKey.ImportECPrivateKey(_bobIdentity.Keys!.SignedPreKey.ExportECPrivateKey(), out _);
        
        // Arrange: Mock the protocol to correctly link the output of the encryption mock with the input of the decryption mock
        var originalMessage = "This is a super secret message.";
        var originalBytes = Encoding.UTF8.GetBytes(originalMessage);
        var encryptedBytes = new byte[] { 1, 2, 3, 4, 5 }; // Dummy encrypted data

        // Act: Alice encrypts a message using stored session state directly
        var encryptedResult = await EncryptViaStoreAsync(_aliceIdentity, conversationId, new Plaintext(originalBytes));

        // Act: Bob decrypts the message by inferring the correct session using stored state
        var decryptedBytes = await TryInferAndDecryptViaStoreAsync(_bobIdentity, encryptedResult, CancellationToken.None);

        // Assert: The decrypted message matches the original
        decryptedBytes.Should().NotBeNull();
        decryptedBytes!.Value.Should().BeEquivalentTo(originalBytes);
        Encoding.UTF8.GetString(decryptedBytes!.Value).Should().Be(originalMessage);
    }

    private (CryptoSharedSecret, CryptoSharedSecret) PerformX3DH()
    {
        // Return dummy shared secrets; actual X3DH handshake is cutover pending
        var aliceSharedSecret = new CryptoSharedSecret(new byte[32]);
        var bobSharedSecret = new CryptoSharedSecret(new byte[32]);
        return (aliceSharedSecret, bobSharedSecret);
    }
}