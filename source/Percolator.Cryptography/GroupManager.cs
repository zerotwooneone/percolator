using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoubleRatchetSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.Cryptography
{
    public class GroupManager : IDisposable
    {
        private readonly Dictionary<string, DoubleRatchetSession> _members = new();
        private readonly ECDsa? _signingKey;
        private readonly byte[]? _creatorSigningPublicKey;
        private readonly ECDiffieHellman _creatorIdentityKey;

        public SenderKeySession GroupSession { get; private set; }
        public string GroupId { get; private set; }
        public byte[]? SigningPublicKey { get; }

        public GroupManager(ECDiffieHellman creatorIdentityKey)
        {
            _creatorIdentityKey = creatorIdentityKey;
            GroupId = Guid.NewGuid().ToString();
            var groupContext = Encoding.UTF8.GetBytes(GroupId);
            GroupSession = new SenderKeySession(null, groupContext);
            _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            SigningPublicKey = _signingKey.ExportSubjectPublicKeyInfo();
        }

        private GroupManager(SenderKeySession groupSession, string groupId, byte[] creatorSigningPublicKey, ECDiffieHellman creatorIdentityKey)
        {
            GroupSession = groupSession;
            GroupId = groupId;
            _creatorSigningPublicKey = creatorSigningPublicKey;
            _creatorIdentityKey = creatorIdentityKey;
        }

        private GroupManager(GroupManagerState state, ECDiffieHellman identityKey)
        {
            _creatorIdentityKey = identityKey;
            _signingKey = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = state.SigningKeyPrivate
            });
            SigningPublicKey = _signingKey.ExportSubjectPublicKeyInfo();

            var groupContext = Encoding.UTF8.GetBytes(state.GroupId);
            var groupSessionState = JsonSerializer.Deserialize<SenderKeySessionState>(state.GroupSessionState)!;
            GroupSession = new SenderKeySession(groupSessionState);
            GroupId = state.GroupId;

            foreach (var (memberId, sessionStateBytes) in state.MemberSessionStates)
            {
                var sessionState = JsonSerializer.Deserialize<DoubleRatchetSessionState>(sessionStateBytes)!;
                _members[memberId] = new DoubleRatchetSession(sessionState);
            }
        }

        public byte[] SaveState(byte[] masterKey)
        {
            if (_signingKey is null) throw new InvalidOperationException("Only the group creator can save state.");

            var memberSessionStates = _members.ToDictionary(
                kvp => kvp.Key,
                kvp => JsonSerializer.SerializeToUtf8Bytes(kvp.Value.GetState())
            );

            var state = new GroupManagerState
            {
                SigningKeyPrivate = _signingKey.ExportParameters(true).D!,
                GroupId = GroupId,
                GroupSessionState = JsonSerializer.SerializeToUtf8Bytes(GroupSession.GetState()),
                MemberSessionStates = memberSessionStates
            };

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
            return CryptoUtils.EncryptAtRest(masterKey, plaintext, Encoding.UTF8.GetBytes("GroupManagerState"));
        }

        public static GroupManager LoadState(byte[] encryptedState, byte[] masterKey, ECDiffieHellman identityKey)
        {
            var plaintext = CryptoUtils.DecryptAtRest(masterKey, encryptedState, Encoding.UTF8.GetBytes("GroupManagerState"));
            var state = JsonSerializer.Deserialize<GroupManagerState>(plaintext)!;
            return new GroupManager(state, identityKey);
        }

        public RatchetMessage CreateInvitation(string memberId, DoubleRatchetSession sessionToMember)
        {
            if (_signingKey is null) throw new InvalidOperationException("Only the group creator can send invitations.");
            _members[memberId] = sessionToMember;
            var unsignedMessage = new UnsignedGroupControlMessage
            {
                SessionKey = GroupSession.SessionKey,
                GroupId = this.GroupId
            };

            var unsignedPayload = JsonSerializer.SerializeToUtf8Bytes(unsignedMessage);
            var signature = _signingKey.SignData(unsignedPayload, HashAlgorithmName.SHA256);

            var signedMessage = new SignedGroupControlMessage
            {
                UnsignedMessage = unsignedPayload,
                Signature = signature
            };

            var signedPayload = JsonSerializer.SerializeToUtf8Bytes(signedMessage);
            return sessionToMember.Encrypt(new Plaintext(signedPayload));
        }

        public Dictionary<string, RatchetMessage> RemoveMember(string memberId)
        {
            if (_signingKey is null) throw new InvalidOperationException("Only the group creator can remove members.");

            if (!_members.Remove(memberId))
            {
                throw new InvalidOperationException("Member not found.");
            }

            var oldGroupId = GroupId;
            // Critical: Create a new group session with a new key and ID.
            GroupId = Guid.NewGuid().ToString();
            var groupContext = Encoding.UTF8.GetBytes(GroupId);
            GroupSession.Dispose(); // Dispose the old session
            GroupSession = new SenderKeySession(null, groupContext);

            var rekeyMessages = new Dictionary<string, RatchetMessage>();
            var unsignedMessage = new UnsignedGroupControlMessage
            {
                OldGroupId = oldGroupId,
                SessionKey = GroupSession.SessionKey,
                GroupId = this.GroupId
            };

            var unsignedPayload = JsonSerializer.SerializeToUtf8Bytes(unsignedMessage);
            var signature = _signingKey.SignData(unsignedPayload, HashAlgorithmName.SHA256);

            var signedMessage = new SignedGroupControlMessage
            {
                UnsignedMessage = unsignedPayload,
                Signature = signature
            };
            var signedPayload = JsonSerializer.SerializeToUtf8Bytes(signedMessage);

            foreach (var (id, session) in _members)
            {
                var rekeyMessage = session.Encrypt(new Plaintext(signedPayload));
                rekeyMessages[id] = rekeyMessage;
            }

            return rekeyMessages;
        }

        public static GroupManager AcceptInvitation(DoubleRatchetSession sessionToCreator, RatchetMessage invitationMessage, byte[] creatorSigningPublicKey, ECDiffieHellman creatorIdentityKey)
        {
            var payload = sessionToCreator.Decrypt(invitationMessage);
            var signedMessage = JsonSerializer.Deserialize<SignedGroupControlMessage>(payload.Value)!;

            using var creatorKey = ECDsa.Create();
            creatorKey.ImportSubjectPublicKeyInfo(creatorSigningPublicKey, out _);
            if (!creatorKey.VerifyData(signedMessage.UnsignedMessage, signedMessage.Signature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("Invalid signature on invitation.");
            }

            var unsignedMessage = JsonSerializer.Deserialize<UnsignedGroupControlMessage>(signedMessage.UnsignedMessage)!;

            if (unsignedMessage.OldGroupId is not null)
            {
                throw new CryptographicException("Invalid invitation message: must not have OldGroupId.");
            }

            var groupContext = Encoding.UTF8.GetBytes(unsignedMessage.GroupId);
            var groupSession = new SenderKeySession(unsignedMessage.SessionKey, groupContext);
            return new GroupManager(groupSession, unsignedMessage.GroupId, creatorSigningPublicKey, creatorIdentityKey);
        }

        public void ProcessRekeyMessage(DoubleRatchetSession sessionToCreator, RatchetMessage rekeyMessage)
        {
            if (_creatorSigningPublicKey is null) throw new InvalidOperationException("Cannot process re-key on a creator's group manager.");

            var payload = sessionToCreator.Decrypt(rekeyMessage);
            var signedMessage = JsonSerializer.Deserialize<SignedGroupControlMessage>(payload.Value)!;

            using var creatorKey = ECDsa.Create();
            creatorKey.ImportSubjectPublicKeyInfo(_creatorSigningPublicKey, out _);
            if (!creatorKey.VerifyData(signedMessage.UnsignedMessage, signedMessage.Signature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("Invalid signature on re-key message.");
            }

            var unsignedMessage = JsonSerializer.Deserialize<UnsignedGroupControlMessage>(signedMessage.UnsignedMessage)!;

            if (unsignedMessage.OldGroupId != GroupId)
            {
                throw new CryptographicException("Re-key message is for a different group.");
            }

            GroupId = unsignedMessage.GroupId;
            var groupContext = Encoding.UTF8.GetBytes(GroupId);
            GroupSession.Dispose();
            GroupSession = new SenderKeySession(unsignedMessage.SessionKey, groupContext);
        }

        public void Dispose()
        {
            _signingKey?.Dispose();
            GroupSession.Dispose();
            foreach (var member in _members.Values)
            {
                member.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
