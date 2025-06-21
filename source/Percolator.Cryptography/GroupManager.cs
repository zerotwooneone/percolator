using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Percolator.Cryptography
{
    public class GroupManager
    {
        private readonly Dictionary<string, SenderKeySession> _groupSessions = new();

        public (RatchetMessage invitation, SenderKeySession creatorSession) CreateGroupAndInvitation(DoubleRatchetSession sessionToMember, string? groupId = null)
        {
            var newGroupId = groupId ?? Guid.NewGuid().ToString();
            var groupContext = Encoding.UTF8.GetBytes(newGroupId);
            var groupSessionKey = RandomNumberGenerator.GetBytes(32);

            var invitation = new GroupInvitationMessage
            {
                GroupId = newGroupId,
                SessionKey = groupSessionKey
            };

            var invitationBytes = JsonSerializer.SerializeToUtf8Bytes(invitation);
            var encryptedInvitation = sessionToMember.Encrypt(invitationBytes);

            var creatorSession = new SenderKeySession(groupSessionKey, groupContext);
            _groupSessions[newGroupId] = creatorSession;

            return (encryptedInvitation, creatorSession);
        }

        public SenderKeySession? AcceptInvitation(DoubleRatchetSession sessionToCreator, RatchetMessage invitationMessage)
        {
            var invitationBytes = sessionToCreator.Decrypt(invitationMessage);
            var invitation = JsonSerializer.Deserialize<GroupInvitationMessage>(invitationBytes);

            if (invitation is null)
            {
                return null;
            }
            
            var groupContext = Encoding.UTF8.GetBytes(invitation.GroupId);
            var memberSession = new SenderKeySession(invitation.SessionKey, groupContext);
            _groupSessions[invitation.GroupId] = memberSession;

            return memberSession;
        }
    }

    internal class GroupInvitationMessage
    {
        public string GroupId { get; set; } = string.Empty;
        public byte[] SessionKey { get; set; } = Array.Empty<byte>();
    }
}
