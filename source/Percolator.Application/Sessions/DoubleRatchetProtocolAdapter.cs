using System.Security.Cryptography;
using System.Text.Json;
using Percolator.Sessions;
using Crypto = Percolator.Cryptography;
using SessionState = Percolator.Sessions.SessionState;
using CryptoSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.Application.Sessions;

public class DoubleRatchetProtocolAdapter : IDoubleRatchetProtocol
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public (SessionState, RatchetEphemeralKey) InitiateSession(RatchetIdentityKey theirIdentityKey, RatchetEphemeralKey theirRatchetKey, SharedSecret sharedSecret)
    {
        var session = Crypto.DoubleRatchetSession.AsInitiator(new Crypto.SharedSecret(sharedSecret.Value), new Crypto.RatchetIdentityKey(theirIdentityKey.Value), new Crypto.RatchetEphemeralKey(theirRatchetKey.Value));
        var state = session.GetState();
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        return (new SessionState(serializedState), new RatchetEphemeralKey(session.RatchetKey.Value));
    }

    public SessionState RespondToSession(RatchetIdentityKey theirIdentityKey, PrivateEphemeralKey ourRatchetKey, SharedSecret sharedSecret)
    {
        using var localRatchetKey = ECDiffieHellman.Create();
        localRatchetKey.ImportECPrivateKey(ourRatchetKey.Value, out _);
        var session = Crypto.DoubleRatchetSession.AsResponder(new Crypto.SharedSecret(sharedSecret.Value), new Crypto.RatchetIdentityKey(theirIdentityKey.Value), localRatchetKey);
        var state = session.GetState();
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        return new SessionState(serializedState);
    }

    public (SessionState newState, RatchetMessage ciphertext) Encrypt(SessionState currentState, Plaintext plaintext)
    {
        var cryptoState = JsonSerializer.Deserialize<CryptoSessionState>(currentState.Value, _jsonOptions)!;
        using var session = new Crypto.DoubleRatchetSession(cryptoState);
        var ciphertext = session.Encrypt(new Crypto.Plaintext(plaintext.Value));
        var newState = session.GetState();
        var serializedNewState = JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions);
        var serializedCiphertext = JsonSerializer.SerializeToUtf8Bytes(ciphertext, _jsonOptions);
        return (new SessionState(serializedNewState), new RatchetMessage(serializedCiphertext));
    }

    public (SessionState newState, Plaintext? plaintext) Decrypt(SessionState currentState, RatchetMessage ciphertext)
    {
        var cryptoState = JsonSerializer.Deserialize<CryptoSessionState>(currentState.Value, _jsonOptions)!;
        using var session = new Crypto.DoubleRatchetSession(cryptoState);
        var cryptoCiphertext = JsonSerializer.Deserialize<Crypto.RatchetMessage>(ciphertext.Value, _jsonOptions)!;
        var plaintext = session.Decrypt(cryptoCiphertext);
        var newState = session.GetState();
        var serializedNewState = JsonSerializer.SerializeToUtf8Bytes(newState, _jsonOptions);
        var sessionsPlaintext = plaintext is not null ? new Plaintext(plaintext.Value) : null;
        return (new SessionState(serializedNewState), sessionsPlaintext);
    }
}
