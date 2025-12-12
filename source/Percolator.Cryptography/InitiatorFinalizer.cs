using System;
using Google.Protobuf;
using Percolator.Contracts;

namespace Percolator.Cryptography;

public interface IInitiatorFinalizer
{
    (string SessionId, RatchetState NewState) Finalize(SharedSecret initialRootKey, SessionRatchetMessage responderMessage);
}

public sealed class InitiatorFinalizer : IInitiatorFinalizer
{
    private readonly IRatchetEngine _ratchet;

    public InitiatorFinalizer(IRatchetEngine ratchet)
    {
        _ratchet = ratchet ?? throw new ArgumentNullException(nameof(ratchet));
    }

    public (string SessionId, RatchetState NewState) Finalize(SharedSecret initialRootKey, SessionRatchetMessage responderMessage)
    {
        if (initialRootKey is null) throw new ArgumentNullException(nameof(initialRootKey));
        if (responderMessage is null) throw new ArgumentNullException(nameof(responderMessage));

        var root = new RootKey(initialRootKey.Value);
        var (send, recv) = RatchetBootstrap.DeriveInitiatorChains(root);

        var state = new RatchetState(
            root,
            send,
            sendingCounter: 0,
            recv,
            receivingCounter: 0,
            previousChainLength: 0,
            remoteRatchetKey: null,
            dhRatchetPrivateKey: null,
            skippedKeyLimit: 1000);

        var ad = new AssociatedData(Array.Empty<byte>());
        var (pt, newState) = _ratchet.Decrypt(state, responderMessage, ad);

        // Parse inner hello to extract session_id
        ResponderInnerHello inner;
        try
        {
            inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOperationException("Invalid responder inner hello payload.", ex);
        }

        if (string.IsNullOrWhiteSpace(inner.DirectSessionId))
        {
            throw new InvalidOperationException("Responder inner hello missing direct_session_id.");
        }

        return (inner.DirectSessionId, newState);
    }
}
