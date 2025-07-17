using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Percolator.Sessions;
using Crypto = Percolator.Cryptography;
using SessionState = Percolator.Sessions.SessionState;
using CryptoSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.Application.Sessions;

public class DoubleRatchetProtocolAdapter : IDoubleRatchetProtocol
{
    private readonly ILogger<DoubleRatchetProtocolAdapter> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    public static readonly bool VerboseLogging = true;  
    
    public DoubleRatchetProtocolAdapter(ILogger<DoubleRatchetProtocolAdapter> logger)
    {
        _logger = logger;
    }

    public SessionState InitiateSession(RatchetIdentityKey theirIdentityKey, RatchetEphemeralKey theirRatchetKey, SharedSecret sharedSecret)
    {
        var session = Crypto.DoubleRatchetSession.AsInitiator(new Crypto.SharedSecret(sharedSecret.Value), new Crypto.RatchetIdentityKey(theirIdentityKey.Value), new Crypto.RatchetEphemeralKey(theirRatchetKey.Value));
        var state = session.GetState();
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        if (VerboseLogging)
        {
            _logger.LogInformation("Initiating double ratchet session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions)));
        }
        return new SessionState(serializedState);
    }

    public SessionState RespondToSession(RatchetIdentityKey theirIdentityKey, PrivateEphemeralKey ourRatchetKey, SharedSecret sharedSecret)
    {
        using var localRatchetKey = ECDiffieHellman.Create();
        localRatchetKey.ImportECPrivateKey(ourRatchetKey.Value, out _);
        var session = Crypto.DoubleRatchetSession.AsResponder(new Crypto.SharedSecret(sharedSecret.Value), new Crypto.RatchetIdentityKey(theirIdentityKey.Value), localRatchetKey);
        var state = session.GetState();
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        if (VerboseLogging)
        {
            _logger.LogInformation("Responding to double ratchet session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions)));
        }
        return new SessionState(serializedState);
    }

    public (SessionState newState, RatchetMessage ciphertext) Encrypt(SessionState currentState, Plaintext plaintext)
    {
        var cryptoState = JsonSerializer.Deserialize<CryptoSessionState>(currentState.Value, _jsonOptions)!;
        using var session = new Crypto.DoubleRatchetSession(cryptoState);
        var ciphertext = session.Encrypt(new Crypto.Plaintext(plaintext.Value));
        var newState = session.GetState();
        var serializedNewState = JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions);
        if (VerboseLogging)
        {
            _logger.LogInformation("After encrypting session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions)));
        }
        var serializedCiphertext = JsonSerializer.SerializeToUtf8Bytes(ciphertext, _jsonOptions);
        return (new SessionState(serializedNewState), new RatchetMessage(serializedCiphertext));
    }

    public (SessionState newState, Plaintext? plaintext) Decrypt(SessionState currentState, RatchetMessage ciphertext)
    {
        var cryptoState = JsonSerializer.Deserialize<CryptoSessionState>(currentState.Value, _jsonOptions)!;
        if (VerboseLogging)
        {
            _logger.LogInformation("Before decrypting session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(cryptoState, _jsonOptions)));
        }
        using var session = new Crypto.DoubleRatchetSession(cryptoState);
        if (VerboseLogging)
        {
            _logger.LogInformation("Rehydrated session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(session.GetState(), _jsonOptions)));
        }
        var cryptoCiphertext = JsonSerializer.Deserialize<Crypto.RatchetMessage>(ciphertext.Value, _jsonOptions)!;
        var plaintext = session.Decrypt(cryptoCiphertext);
        var newState = session.GetState();
        var serializedNewState = JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions);
        if (VerboseLogging)
        {
            _logger.LogInformation("After decrypting session {Session}", Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions)));
        }
        var sessionsPlaintext = new Plaintext(plaintext.Value);
        return (new SessionState(serializedNewState), sessionsPlaintext);
    }
}
