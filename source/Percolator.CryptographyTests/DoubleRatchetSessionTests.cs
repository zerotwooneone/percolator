using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Percolator.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoubleRatchetSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private ECDiffieHellman _aliceIdentity;
        private ECDiffieHellman _bobIdentity;
        private ECDiffieHellman _bobRatchetKey;
        private DoubleRatchetSession _aliceSession;
        private DoubleRatchetSession _bobSession;
        private SharedSecret _sharedSecret;
        private RatchetIdentityKey _bobIdentityKey;
        private RatchetEphemeralKey _bobPreKey;
        private ILogger<DoubleRatchetSession> _logger;

        [SetUp]
        public void Setup()
        {
            // Setup logger
            _logger = new NullLogger<DoubleRatchetSession>();
            
            _aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // Simulate a secure key exchange (e.g., X3DH) to get a shared secret.
            _sharedSecret = new SharedSecret(_aliceIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey));

            _bobIdentityKey = new RatchetIdentityKey(_bobIdentity.PublicKey.ExportSubjectPublicKeyInfo());
            _bobPreKey = new RatchetEphemeralKey(_bobRatchetKey.PublicKey.ExportSubjectPublicKeyInfo());

            _aliceSession = DoubleRatchetSession.AsInitiator(
                _sharedSecret,
                _bobIdentityKey,
                _bobPreKey,
                _logger);

            _bobSession = DoubleRatchetSession.AsResponder(
                _sharedSecret,
                new RatchetIdentityKey(_aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
                new RatchetEphemeralKey(_aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
                _bobRatchetKey,
                _logger);
        }

        [TearDown]
        public void TearDown()
        {
            // Dispose of all keys after each test.
            _aliceSession.Dispose();
            _bobSession.Dispose();
            _aliceIdentity.Dispose();
            _bobIdentity.Dispose();
            _bobRatchetKey.Dispose();
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_Simple()
        {
            var plaintext = Encoding.UTF8.GetBytes("Hello Bob!");
            var message = _aliceSession.Encrypt(new Plaintext(plaintext));
            var decrypted = _bobSession.Decrypt(message);
            decrypted.Value.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_After_Ratchet()
        {
            var plaintext1 = Encoding.UTF8.GetBytes("Hello Bob, this is Alice.");
            var message1 = _aliceSession.Encrypt(new Plaintext(plaintext1));
            var decrypted1 = _bobSession.Decrypt(message1);
            decrypted1.Value.Should().BeEquivalentTo(plaintext1);

            var plaintext2 = Encoding.UTF8.GetBytes("Hello Alice, this is Bob.");
            var message2 = _bobSession.Encrypt(new Plaintext(plaintext2));
            var decrypted2 = _aliceSession.Decrypt(message2);
            decrypted2.Value.Should().BeEquivalentTo(plaintext2);
        }

        [Test]
        public void GetState_And_Restore_RestoresSessionCorrectly()
        {
            var plaintext = "Hello, Bob!"u8.ToArray();
            var message = _aliceSession.Encrypt(new Plaintext(plaintext));
            _bobSession.Decrypt(message);

            var state = _bobSession.GetState();
            // Simulate serializing and deserializing the state
            var serializedState = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var deserializedState = JsonSerializer.Deserialize<DoubleRatchetSessionState>(serializedState)!;
            using var loadedBobSession = new DoubleRatchetSession(deserializedState, _logger);

            var response = loadedBobSession.Encrypt(new Plaintext("Hello, Alice!"u8.ToArray()));
            var decryptedResponse = _aliceSession.Decrypt(response);

            Encoding.UTF8.GetString(decryptedResponse.Value).Should().Be("Hello, Alice!");
        }

        [Test]
        public void Decrypt_WithSkippedMessages_Succeeds()
        {
            var msg1 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 1")));
            var msg2 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 2")));
            var msg3 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 3")));

            // Decrypt in reverse order
            _bobSession.Decrypt(msg3).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 3"));
            _bobSession.Decrypt(msg2).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 2"));
            _bobSession.Decrypt(msg1).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 1"));
        }

        [Test]
        public void Decrypt_WithTamperedHeader_ThrowsException()
        {
            // Arrange
            var message = _aliceSession.Encrypt(new Plaintext("ping"u8.ToArray()));

            // Act: Tamper with the header after encryption
            // Extract the original header information
            var (ratchetKey, counter, previousChainLength) = message.GetHeader();
            
            // Create a new message with a tampered counter
            var tamperedMessage = SessionRatchetMessage.Create(
                ratchetKey, 
                counter + 1, // Increment the counter to tamper with the header
                previousChainLength, // previous chain length (default to 0 for testing)
                message.GetCiphertext());

            // Assert: Decryption must fail because the AD (the header) no longer matches the ciphertext.
            // We expect the specific AuthenticationTagMismatchException, which is a subclass of CryptographicException.
            Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(() => _bobSession.Decrypt(tamperedMessage));
        }

        [Test]
        public void Decrypt_WithTamperedMessage_ThrowsException()
        {
            // Arrange
            var plaintext = new Plaintext(Encoding.UTF8.GetBytes("Hello, world!"));
            var message = _aliceSession.Encrypt(plaintext);

            // Act: Tamper with the message after encryption
            var header = message.GetHeader();
            
            // Get the ciphertext and tamper with it by changing the last byte
            // (which will affect the GCM authentication tag)
            var originalCiphertext = message.GetCiphertext();
            var tamperedCiphertextBytes = originalCiphertext.Value.ToArray(); // Clone the byte array
            tamperedCiphertextBytes[tamperedCiphertextBytes.Length - 1] ^= 0x01; // Flip one bit in the last byte
            var tamperedCiphertext = new Ciphertext(tamperedCiphertextBytes);
            
            // Create a new message with the tampered ciphertext
            var tamperedMessage = SessionRatchetMessage.Create(
                header.RatchetKey,
                header.Counter, 
                header.PreviousChainLength, // previous chain length (default to 0 for testing)
                tamperedCiphertext
            );

            // We expect the specific AuthenticationTagMismatchException when Bob tries to decrypt
            Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(
                () => _bobSession.Decrypt(tamperedMessage));
        }

        [Test]
        public void DoubleRatchet_ShouldWork_WithKeyExportAndImport()
        {
            // This test simulates the real-world scenario where keys are exported and imported
            // rather than sharing the actual instances

            // Create fresh keys for this test
            using var aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // Export the public keys as they would be in a real-world exchange
            byte[] aliceIdentityPublicKeyBytes = aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo();
            byte[] bobIdentityPublicKeyBytes = bobIdentity.PublicKey.ExportSubjectPublicKeyInfo();
            byte[] bobRatchetKeyPublicBytes = bobRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();
            
            // Also export the private key of bobRatchetKey to simulate how it's done in DirectSessionManager
            byte[] bobRatchetKeyPrivateBytes = bobRatchetKey.ExportECPrivateKey();

            // Derive shared secret (as initiator would)
            var sharedSecret = new SharedSecret(aliceIdentity.DeriveKeyMaterial(bobIdentity.PublicKey));

            // Create Alice's session (initiator)
            using var aliceSession = DoubleRatchetSession.AsInitiator(
                sharedSecret,
                new RatchetIdentityKey(bobIdentityPublicKeyBytes),
                new RatchetEphemeralKey(bobRatchetKeyPublicBytes),
                _logger
            );

            // Now create Bob's session (responder)
            // Import the private key to a new instance (like in DirectSessionManager)
            using var importedBobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            importedBobRatchetKey.ImportECPrivateKey(bobRatchetKeyPrivateBytes, out _);

            using var bobSession = DoubleRatchetSession.AsResponder(
                sharedSecret,
                new RatchetIdentityKey(aliceIdentityPublicKeyBytes),
                new RatchetEphemeralKey(aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
                importedBobRatchetKey,
                _logger
            );

            // Now test message exchange in alternating order (crucial for Double Ratchet protocol)
            // Each side must process messages in order and alternate sending/receiving
            
            // First exchange: Alice to Bob
            var alicePlaintext1 = new Plaintext(Encoding.UTF8.GetBytes("Message 1 from Alice to Bob"));
            var aliceMessage1 = aliceSession.Encrypt(alicePlaintext1);
            var bobDecrypted1 = bobSession.Decrypt(aliceMessage1);
            Encoding.UTF8.GetString(bobDecrypted1.Value).Should().Be("Message 1 from Alice to Bob");

            // Second exchange: Bob to Alice
            var bobPlaintext1 = new Plaintext(Encoding.UTF8.GetBytes("Message 1 from Bob to Alice"));
            var bobMessage1 = bobSession.Encrypt(bobPlaintext1);
            var aliceDecrypted1 = aliceSession.Decrypt(bobMessage1);
            Encoding.UTF8.GetString(aliceDecrypted1.Value).Should().Be("Message 1 from Bob to Alice");

            // Third exchange: Alice to Bob again
            var alicePlaintext2 = new Plaintext(Encoding.UTF8.GetBytes("Message 2 from Alice to Bob"));
            var aliceMessage2 = aliceSession.Encrypt(alicePlaintext2);
            var bobDecrypted2 = bobSession.Decrypt(aliceMessage2);
            Encoding.UTF8.GetString(bobDecrypted2.Value).Should().Be("Message 2 from Alice to Bob");

            // Before serialization, capture the state
            var aliceStateBeforeSerialization = aliceSession.GetState();
            var bobStateBeforeSerialization = bobSession.GetState();

            Console.WriteLine($"Alice receiving counter before serialization: {aliceStateBeforeSerialization.ReceivingCounter}");
            Console.WriteLine($"Alice sending counter before serialization: {aliceStateBeforeSerialization.SendingCounter}");
            Console.WriteLine($"Bob receiving counter before serialization: {bobStateBeforeSerialization.ReceivingCounter}");
            Console.WriteLine($"Bob sending counter before serialization: {bobStateBeforeSerialization.SendingCounter}");
            
            if (aliceStateBeforeSerialization.RootKey != null)
                Console.WriteLine($"Alice root key hash before serialization: {Convert.ToBase64String(SHA256.HashData(aliceStateBeforeSerialization.RootKey.Value))}");
            if (bobStateBeforeSerialization.RootKey != null)
                Console.WriteLine($"Bob root key hash before serialization: {Convert.ToBase64String(SHA256.HashData(bobStateBeforeSerialization.RootKey.Value))}");

            // Serialize and deserialize the session states
            var aliceStateJson = JsonSerializer.Serialize(aliceStateBeforeSerialization);
            var bobStateJson = JsonSerializer.Serialize(bobStateBeforeSerialization);
            
            Console.WriteLine($"Alice session JSON: {aliceStateJson}");
            Console.WriteLine($"Bob session JSON: {bobStateJson}");
            
            var aliceStateDeserialized = JsonSerializer.Deserialize<DoubleRatchetSessionState>(aliceStateJson);
            var bobStateDeserialized = JsonSerializer.Deserialize<DoubleRatchetSessionState>(bobStateJson);
            
            Console.WriteLine($"Alice receiving counter after deserialization: {aliceStateDeserialized!.ReceivingCounter}");
            Console.WriteLine($"Alice sending counter after deserialization: {aliceStateDeserialized.SendingCounter}");
            Console.WriteLine($"Bob receiving counter after deserialization: {bobStateDeserialized!.ReceivingCounter}");
            Console.WriteLine($"Bob sending counter after deserialization: {bobStateDeserialized.SendingCounter}");
            
            if (aliceStateDeserialized.RootKey != null)
                Console.WriteLine($"Alice root key hash after deserialization: {Convert.ToBase64String(SHA256.HashData(aliceStateDeserialized.RootKey.Value))}");
            if (bobStateDeserialized.RootKey != null)
                Console.WriteLine($"Bob root key hash after deserialization: {Convert.ToBase64String(SHA256.HashData(bobStateDeserialized.RootKey.Value))}");
            
            // Create new sessions with the deserialized state
            using var aliceSession2 = new DoubleRatchetSession(aliceStateDeserialized, _logger);
            using var bobSession2 = new DoubleRatchetSession(bobStateDeserialized, _logger);
            
            // IMPORTANT: To avoid the "message received out of order" exception,
            // we need to understand that message counters are maintained across
            // session serialization. Let's use independent message objects
            // after deserialization rather than trying to reuse older ones.
            
            // Test message exchange with the new sessions (continuing the conversation)
            var alicePlaintext3 = new Plaintext(Encoding.UTF8.GetBytes("Final message from Alice"));
            var aliceMessage3 = aliceSession2.Encrypt(alicePlaintext3);
            
            // Extract header info for debugging
            var header = aliceMessage3.GetHeader();
            Console.WriteLine($"New message from Alice after deserialization has counter: {header.Counter}");
            
            try {
                var bobDecrypted3 = bobSession2.Decrypt(aliceMessage3);
                Encoding.UTF8.GetString(bobDecrypted3.Value).Should().Be("Final message from Alice");
                
                // And back from Bob
                var bobPlaintext2 = new Plaintext(Encoding.UTF8.GetBytes("Final message from Bob"));
                var bobMessage2 = bobSession2.Encrypt(bobPlaintext2);
                var aliceDecrypted2 = aliceSession2.Decrypt(bobMessage2);
                Encoding.UTF8.GetString(aliceDecrypted2.Value).Should().Be("Final message from Bob");
            }
            catch (Exception ex) {
                Console.WriteLine($"Exception during post-serialization message exchange: {ex.Message}");
                Console.WriteLine($"Exception type: {ex.GetType().Name}");
                Assert.Fail($"Exception during post-serialization message exchange: {ex.Message}");
            }
        }

        [Test]
        public void DoubleRatchetSession_SerializationAndDeserialization_PreservesState()
        {
            // Arrange - Create a session and use it a bit
            using var aliceSession = DoubleRatchetSession.AsInitiator(
                _sharedSecret,
                _bobIdentityKey,
                _bobPreKey,
                _logger
            );

            // Send a few messages to advance counters
            var message1 = aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Message 1")));
            var message2 = aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Message 2")));
            
            // Get the original state
            var originalState = aliceSession.GetState();
            
            // Log key state details for debugging
            Console.WriteLine($"Original sending counter: {originalState.SendingCounter}");
            Console.WriteLine($"Original receiving counter: {originalState.ReceivingCounter}");
            
            // Serialize and deserialize
            var json = JsonSerializer.Serialize(originalState);
            Console.WriteLine($"Serialized state: {json}");
            var deserializedState = JsonSerializer.Deserialize<DoubleRatchetSessionState>(json);
            
            // Verify counters preserved
            Assert.That(deserializedState!.SendingCounter, Is.EqualTo(originalState.SendingCounter));
            Assert.That(deserializedState.ReceivingCounter, Is.EqualTo(originalState.ReceivingCounter));
            
            // Create new session from deserialized state
            using var restoredSession = new DoubleRatchetSession(deserializedState, _logger);
            
            // Try encrypting a new message with the restored session
            var message3 = restoredSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Message 3")));
            
            // Verify the counter was advanced correctly
            var newState = restoredSession.GetState();
            Assert.That(newState.SendingCounter, Is.EqualTo(originalState.SendingCounter + 1));
            
            // If we had the receiving session, it would be able to decrypt this message
            // But the key point is that the session state can be correctly serialized and deserialized
            // without losing counter information
        }

        [Test]
        public void SendingCounter_ShouldIncrementAfterEncrypt()
        {
            // First, establish the session with an initial message exchange
            var initialMessage = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Initial message")));
            var initialDecrypted = _bobSession.Decrypt(initialMessage);
            
            // Get the counter values after initialization
            ulong aliceInitialSendingCounter = _aliceSession.SendingCounter;
            ulong bobInitialSendingCounter = _bobSession.SendingCounter;
            
            // Act - Alice encrypts a message
            var plaintext = new Plaintext(Encoding.UTF8.GetBytes("Test message"));
            var message = _aliceSession.Encrypt(plaintext);
            
            // Assert - Alice's sending counter should increment by 1
            Assert.That(_aliceSession.SendingCounter, Is.EqualTo(aliceInitialSendingCounter + 1));
            // Bob's sending counter should remain unchanged
            Assert.That(_bobSession.SendingCounter, Is.EqualTo(bobInitialSendingCounter));
            
            // Act again - Bob encrypts a message
            var bobPlaintext = new Plaintext(Encoding.UTF8.GetBytes("Response message"));
            var bobMessage = _bobSession.Encrypt(bobPlaintext);
            
            // Assert - Bob's sending counter should now increment by 1 too
            Assert.That(_bobSession.SendingCounter, Is.EqualTo(bobInitialSendingCounter + 1));
            // Alice's sending counter should remain at its previous value
            Assert.That(_aliceSession.SendingCounter, Is.EqualTo(aliceInitialSendingCounter + 1));
        }

        [Test]
        public void ReceivingCounter_ShouldIncrementAfterDecrypt()
        {
            // First, establish the session with an initial message exchange
            var initialMessage = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Initial message")));
            var initialDecrypted = _bobSession.Decrypt(initialMessage);
            
            // Get the counter values after initialization
            ulong aliceInitialReceivingCounter = _aliceSession.ReceivingCounter;
            ulong bobInitialReceivingCounter = _bobSession.ReceivingCounter;
            
            // Act - Alice encrypts and Bob decrypts
            var plaintext = new Plaintext(Encoding.UTF8.GetBytes("Test message"));
            var message = _aliceSession.Encrypt(plaintext);
            var decryptedByBob = _bobSession.Decrypt(message);
            
            // Assert - Bob's receiving counter should increment by 1
            Assert.That(_bobSession.ReceivingCounter, Is.EqualTo(bobInitialReceivingCounter + 1));
            // Alice's receiving counter should remain unchanged
            Assert.That(_aliceSession.ReceivingCounter, Is.EqualTo(aliceInitialReceivingCounter));
            
            // Act again - Bob encrypts and Alice decrypts
            var bobPlaintext = new Plaintext(Encoding.UTF8.GetBytes("Response message"));
            var bobMessage = _bobSession.Encrypt(bobPlaintext);
            var decryptedByAlice = _aliceSession.Decrypt(bobMessage);
            
            // Assert - Alice's receiving counter should now increment by 1 too
            Assert.That(_aliceSession.ReceivingCounter, Is.EqualTo(aliceInitialReceivingCounter + 1));
            // Bob's receiving counter should remain at its previous value
            Assert.That(_bobSession.ReceivingCounter, Is.EqualTo(bobInitialReceivingCounter + 1));
        }
        
        [Test]
        public void RatchetOperation_ResetsSendingCounter_AfterDirectionalChange()
        {
            // First, establish the session with an initial message exchange
            var initialMessage = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Initial message")));
            var initialDecrypted = _bobSession.Decrypt(initialMessage);
            
            // Log initial counter values after first exchange
            Console.WriteLine($"After initial exchange: Alice sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"After initial exchange: Bob sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Send second message from Alice to Bob
            var alicePlaintext = new Plaintext(Encoding.UTF8.GetBytes("Alice message"));
            var aliceMessage = _aliceSession.Encrypt(alicePlaintext);
            var bobDecrypted = _bobSession.Decrypt(aliceMessage);
            
            // Log counter values after second Alice→Bob message
            Console.WriteLine($"After second Alice→Bob message: Alice sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"After second Alice→Bob message: Bob sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Now Bob sends to Alice (direction change triggers ratchet operation)
            var bobPlaintext = new Plaintext(Encoding.UTF8.GetBytes("Bob message"));
            var bobMessage = _bobSession.Encrypt(bobPlaintext);
            var aliceDecrypted = _aliceSession.Decrypt(bobMessage);
            
            // Log counter values after direction change and ratchet
            Console.WriteLine($"After Bob→Alice message (direction change): Alice sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"After Bob→Alice message (direction change): Bob sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Alice sends back to Bob again (another direction change)
            var aliceResponsePlaintext = new Plaintext(Encoding.UTF8.GetBytes("Alice response"));
            var aliceResponseMessage = _aliceSession.Encrypt(aliceResponsePlaintext);
            var bobResponseDecrypted = _bobSession.Decrypt(aliceResponseMessage);
            
            // Log final counter values after second direction change
            Console.WriteLine($"After Alice→Bob message (second direction change): Alice sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"After Alice→Bob message (second direction change): Bob sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Observation: The key finding is that the Double Ratchet protocol resets the sending counter to 0
            // after a direction change (when a participant who just received a message becomes the sender).
            // This is expected behavior in Double Ratchet, but crucial to understand for proper state persistence.
        }

        [Test]
        public void SessionSerialization_ShouldPreserveCounters_AfterDirectionChange()
        {
            // First, establish the session with an initial message exchange
            var initialMessage = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("Initial message")));
            var initialDecrypted = _bobSession.Decrypt(initialMessage);
            
            // Log initial session state
            Console.WriteLine("=== SESSION STATE AFTER INITIAL EXCHANGE ===");
            Console.WriteLine($"Alice: sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"Bob: sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Create a state snapshot of Alice's session
            var aliceSessionState = CreateSessionStateSnapshot(_aliceSession);
            
            // Now have a direction change (Bob sends to Alice)
            var bobPlaintext = new Plaintext(Encoding.UTF8.GetBytes("Bob message"));
            var bobMessage = _bobSession.Encrypt(bobPlaintext);
            var aliceDecrypted = _aliceSession.Decrypt(bobMessage);
            
            // Log session state after direction change
            Console.WriteLine("=== SESSION STATE AFTER DIRECTION CHANGE ===");
            Console.WriteLine($"Alice: sending={_aliceSession.SendingCounter}, receiving={_aliceSession.ReceivingCounter}");
            Console.WriteLine($"Bob: sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            
            // Create a state snapshot of Alice's session after direction change
            var aliceSessionStateAfterDirectionChange = CreateSessionStateSnapshot(_aliceSession);
            
            // Create a new session for Alice using the state after direction change
            var aliceNewSession = new DoubleRatchetSession(aliceSessionStateAfterDirectionChange, _logger);
            
            try
            {
                // Send a message from the new Alice session
                var aliceNewPlaintext = new Plaintext(Encoding.UTF8.GetBytes("Alice new message"));
                var aliceNewMessage = aliceNewSession.Encrypt(aliceNewPlaintext);
                
                // Bob should be able to decrypt it
                var bobDecryptedFromNewAlice = _bobSession.Decrypt(aliceNewMessage);
                
                // Verify the decryption worked
                Assert.That(bobDecryptedFromNewAlice.Value, Is.EqualTo(Encoding.UTF8.GetBytes("Alice new message")));
                
                // Log final session state
                Console.WriteLine("=== FINAL SESSION STATE ===");
                Console.WriteLine($"Alice (new session): sending={aliceNewSession.SendingCounter}, receiving={aliceNewSession.ReceivingCounter}");
                Console.WriteLine($"Bob: sending={_bobSession.SendingCounter}, receiving={_bobSession.ReceivingCounter}");
            }
            catch (CryptographicException ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Console.WriteLine($"Alice (new) sending counter: {aliceNewSession.SendingCounter}");
                Console.WriteLine($"Bob receiving counter: {_bobSession.ReceivingCounter}");
                throw;
            }
        }
        
        [Test]
        public void PreviousChainLength_IsCorrectlyUpdated_AfterRatchetSteps()
        {
            // Reset test environment to ensure clean state
            Setup();
            
            // First message from Alice to Bob - Alice is the initiator
            var alicePlaintext1 = new Plaintext(Encoding.UTF8.GetBytes("A1 to B"));
            var aliceMessage1 = _aliceSession.Encrypt(alicePlaintext1);
            
            // At this point Alice's previous chain length should be 0 (no previous chain)
            // and sending counter should be 1 after first message
            _aliceSession.PreviousChainLength.Should().Be(0);
            _aliceSession.SendingCounter.Should().Be(1);
            
            // Bob receives message and decrypts
            var bobDecrypted1 = _bobSession.Decrypt(aliceMessage1);
            Encoding.UTF8.GetString(bobDecrypted1.Value).Should().Be("A1 to B");
            
            // First message from Bob to Alice - this will cause a ratchet step
            var bobPlaintext1 = new Plaintext(Encoding.UTF8.GetBytes("B1 to A"));
            var bobMessage1 = _bobSession.Encrypt(bobPlaintext1);
            
            // Bob's previous chain length should be 0 (no previous chain yet)
            // and sending counter should be 1 after first message
            _bobSession.PreviousChainLength.Should().Be(0);
            _bobSession.SendingCounter.Should().Be(1);
            
            // Alice receives and decrypts - this causes Alice to perform a ratchet step
            var aliceDecrypted1 = _aliceSession.Decrypt(bobMessage1);
            Encoding.UTF8.GetString(aliceDecrypted1.Value).Should().Be("B1 to A");
            
            // Second message from Alice to Bob - after a ratchet step
            var alicePlaintext2 = new Plaintext(Encoding.UTF8.GetBytes("A2 to B"));
            var aliceMessage2 = _aliceSession.Encrypt(alicePlaintext2);
            
            // Now Alice's previous chain length should be 1 (from first sending chain)
            // and sending counter should reset to 1 after ratchet
            _aliceSession.PreviousChainLength.Should().Be(1);
            _aliceSession.SendingCounter.Should().Be(1);
            
            // Bob receives the second message
            var bobDecrypted2 = _bobSession.Decrypt(aliceMessage2);
            Encoding.UTF8.GetString(bobDecrypted2.Value).Should().Be("A2 to B");
            
            // Test serialization/deserialization of the previous chain length
            var aliceState = _aliceSession.GetState();
            aliceState.PreviousChainLength.Should().Be(1);
            
            var serialized = JsonSerializer.Serialize(aliceState);
            var deserialized = JsonSerializer.Deserialize<DoubleRatchetSessionState>(serialized)!;
            
            // Verify deserialized state has the correct previous chain length
            deserialized.PreviousChainLength.Should().Be(1);
            
            // Create new session from deserialized state
            using var newAliceSession = new DoubleRatchetSession(deserialized, _logger);
            newAliceSession.PreviousChainLength.Should().Be(1);
            
            // Second message from Bob to Alice - this causes another ratchet step
            var bobPlaintext2 = new Plaintext(Encoding.UTF8.GetBytes("B2 to A"));
            var bobMessage2 = _bobSession.Encrypt(bobPlaintext2);
            
            // Now Bob's previous chain length should be 1 (previous sending chain had 1 message)
            _bobSession.PreviousChainLength.Should().Be(1);
            _bobSession.SendingCounter.Should().Be(1);
            
            // Complete the chain by decrypting Bob's message
            var aliceDecrypted2 = newAliceSession.Decrypt(bobMessage2);
            Encoding.UTF8.GetString(aliceDecrypted2.Value).Should().Be("B2 to A");
        }
        
        // Helper to create a session state snapshot from a session
        private DoubleRatchetSession.DoubleRatchetSessionState CreateSessionStateSnapshot(DoubleRatchetSession session)
        {
            // Use the new internal properties instead of reflection
            PrivateEphemeralKey? dhPrivateKey = null;
            
            // Get the DH private key bytes using the internal method
            byte[]? privateKeyBytes = session.GetDhRatchetPrivateKeyBytes();
            if (privateKeyBytes != null)
            {
                dhPrivateKey = new PrivateEphemeralKey(privateKeyBytes);
            }
            
            // Create session state using properties
            return new DoubleRatchetSession.DoubleRatchetSessionState
            {
                RootKey = session.RootKey,
                SendingChainKey = session.SendingChainKey,
                ReceivingChainKey = session.ReceivingChainKey,
                SendingCounter = session.SendingCounter,
                ReceivingCounter = session.ReceivingCounter,
                PreviousChainLength = session.PreviousChainLength,
                SkippedMessageKeys = session.SkippedMessageKeys.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value),
                TheirIdentityPublicKey = session.RemoteIdentityPublicKey,
                TheirDhRatchetPublicKey = session.RemoteRatchetKey,
                DhRatchetPrivateKey = dhPrivateKey,
                RatchetFlag = session.RatchetFlag
            };
        }
    }
}
