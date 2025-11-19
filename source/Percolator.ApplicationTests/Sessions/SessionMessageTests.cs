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
        IDoubleRatchetSessionStore store,
        ActiveIdentityContext identity,
        SessionId sessionId,
        Plaintext plaintext)
    {
        if (identity.Identity is null) throw new InvalidOperationException("Identity context not loaded");
        var state = await store.GetSessionStateAsync(sessionId, identity.Identity.SelfIdentityId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Session state not found");
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        using var session = new DoubleRatchetSession(state, sessionLogger, _options);
        var cipher = session.Encrypt(plaintext);
        await store.SetSessionStateAsync(sessionId, session.GetState(), identity.Identity.SelfIdentityId).ConfigureAwait(false);
        return cipher;
    }

    private async Task<Plaintext> TryInferAndDecryptViaStoreAsync(
        IDoubleRatchetSessionStore store,
        ActiveIdentityContext identity,
        SessionRatchetMessage encrypted,
        CancellationToken ct)
    {
        if (identity.Identity is null) throw new InvalidOperationException("Identity context not loaded");
        var selfId = identity.Identity.SelfIdentityId;
        var sessionIds = await store.GetAllSessionIdsAsync(selfId).ConfigureAwait(false);
        foreach (var sid in sessionIds)
        {
            var state = await store.GetSessionStateAsync(sid, selfId).ConfigureAwait(false);
            if (state is null) continue;
            var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
            using var session = new DoubleRatchetSession(state, sessionLogger, _options);
            try
            {
                var pt = session.Decrypt(encrypted);
                await store.SetSessionStateAsync(sid, session.GetState(), selfId).ConfigureAwait(false);
                return pt;
            }
            catch
            {
                // ignore and continue trying other sessions
            }
        }
        throw new InvalidOperationException("No matching session could decrypt the message");
    }
    private IDoubleRatchetSessionStore _aliceSessionStore = null!;
    private IDoubleRatchetSessionStore _bobSessionStore = null!;
    private DirectSessionManager _aliceManager = null!;
    private DirectSessionManager _bobManager = null!;
    private ActiveIdentityContext _aliceIdentity = null!;
    private ActiveIdentityContext _bobIdentity = null!;
    private ILoggerFactory _loggerFactory = null!;
    private IOptions<CryptographyOptions> _options = null!;
    private ECDiffieHellman _aliceEphemeral;

    [SetUp]
    public void SetUp()
    {
        _aliceSessionStore = new FakeDoubleRatchetSessionStore();
        _bobSessionStore = new FakeDoubleRatchetSessionStore();

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
        
        // Create managers with real loggers for diagnostic output
        _aliceManager = new DirectSessionManager(
            _aliceSessionStore,
            _aliceIdentity,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory,
            _options);
        
        _bobManager = new DirectSessionManager(
            _bobSessionStore, 
            _bobIdentity,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory,
            _options);
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
        await _aliceManager.EstablishSessionAsInitiatorAsync(
            conversationId, 
            bobIdentityKey, 
            bobRatchetKey, 
            new CryptoSharedSecret(aliceSharedSecret.Value), 
            _aliceEphemeral);

        var alicePeerId = new PeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new CryptoRatchetIdentityKey(_aliceIdentity.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
        var aliceEphemeralKey = new RatchetEphemeralKey(_aliceIdentity.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        // Create the ECDiffieHellman key using Bob's SignedPreKey that was used in the handshake
        using var bobHandshakeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        bobHandshakeKey.ImportECPrivateKey(_bobIdentity.Keys!.SignedPreKey.ExportECPrivateKey(), out _);
        
        await _bobManager.EstablishSessionAsResponderAsync(conversationId, aliceIdentityKey, aliceEphemeralKey, bobHandshakeKey, new CryptoSharedSecret(bobSharedSecret.Value));
        
        // Arrange: Mock the protocol to correctly link the output of the encryption mock with the input of the decryption mock
        var originalMessage = "This is a super secret message.";
        var originalBytes = Encoding.UTF8.GetBytes(originalMessage);
        var encryptedBytes = new byte[] { 1, 2, 3, 4, 5 }; // Dummy encrypted data

        // Act: Alice encrypts a message using stored session state directly
        var encryptedResult = await EncryptViaStoreAsync(_aliceSessionStore, _aliceIdentity, conversationId, new Plaintext(originalBytes));

        // Act: Bob decrypts the message by inferring the correct session using stored state
        var decryptedBytes = await TryInferAndDecryptViaStoreAsync(_bobSessionStore, _bobIdentity, encryptedResult, CancellationToken.None);

        // Assert: The decrypted message matches the original
        decryptedBytes.Should().NotBeNull();
        decryptedBytes!.Value.Should().BeEquivalentTo(originalBytes);
        Encoding.UTF8.GetString(decryptedBytes!.Value).Should().Be(originalMessage);
    }

    private (CryptoSharedSecret, CryptoSharedSecret) PerformX3DH()
    {
        var x3dhManager = new X3DHManager(_loggerFactory.CreateLogger<X3DHManager>(), _options);

        // Alice (initiator) keys
        var aliceIdentityKey = _aliceIdentity.Keys!.IdentitySigningKey;
        var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob (responder) keys
        var bobIdentitySigningKey = _bobIdentity.Keys!.IdentitySigningKey;
        var bobSignedPreKey = _bobIdentity.Keys!.SignedPreKey;
        var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Alice receives Bob's pre-key bundle
        var bobPreKeyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey( bobIdentitySigningKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey( bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new OneTimeKey( bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        );

        // Alice initiates the handshake to calculate her shared secret
        var aliceSharedSecret = x3dhManager.InitiateHandshake(bobPreKeyBundle, aliceEphemeralKey, aliceIdentityKey);

        // Bob receives Alice's initial message info and calculates his shared secret
        var bobSharedSecret = x3dhManager.RespondToHandshake(
            new CryptoRatchetIdentityKey(aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new CryptoRatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new CryptoRatchetIdentityKey(bobIdentitySigningKey.ExportECPrivateKey()),
            new CryptoPrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            new CryptoPrivateOneTimeKey(bobOneTimePreKey.ExportECPrivateKey())
        );

        aliceEphemeralKey.Dispose();
        bobOneTimePreKey.Dispose();

        return (aliceSharedSecret, bobSharedSecret);
    }
}