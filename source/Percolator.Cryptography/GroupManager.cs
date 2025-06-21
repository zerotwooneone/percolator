using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Percolator.Cryptography
{
    public class GroupManager
    {
        private readonly Dictionary<string, DoubleRatchetSession> _members = new();
        public SenderKeySession GroupSession { get; private set; }
        public string GroupId { get; private set; }

        public GroupManager()
        {
            GroupId = Guid.NewGuid().ToString();
            var groupContext = Encoding.UTF8.GetBytes(GroupId);
            GroupSession = new SenderKeySession(null, groupContext);
        }

        public GroupManager(SenderKeySession groupSession, string groupId)
        {
            GroupSession = groupSession;
            GroupId = groupId;
        }

        public RatchetMessage CreateInvitation(string memberId, DoubleRatchetSession sessionToMember)
        {
            _members[memberId] = sessionToMember;
            var controlMessage = new GroupControlMessage
            {
                SessionKey = GroupSession.SessionKey,
                GroupId = this.GroupId
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(controlMessage);
            return sessionToMember.Encrypt(payload);
        }

        public Dictionary<string, RatchetMessage> RemoveMember(string memberId)
        {
            if (!_members.Remove(memberId))
            {
                throw new InvalidOperationException("Member not found.");
            }

            // Critical: Create a new group session with a new key and ID.
            GroupId = Guid.NewGuid().ToString();
            var groupContext = Encoding.UTF8.GetBytes(GroupId);
            GroupSession = new SenderKeySession(null, groupContext);

            var rekeyMessages = new Dictionary<string, RatchetMessage>();
            var controlMessage = new GroupControlMessage
            {
                SessionKey = GroupSession.SessionKey,
                GroupId = this.GroupId
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(controlMessage);

            foreach (var (id, session) in _members)
            {
                var rekeyMessage = session.Encrypt(payload);
                rekeyMessages[id] = rekeyMessage;
            }

            return rekeyMessages;
        }

        public static GroupManager AcceptInvitation(DoubleRatchetSession sessionToCreator, RatchetMessage invitationMessage)
        {
            var payload = sessionToCreator.Decrypt(invitationMessage);
            var controlMessage = JsonSerializer.Deserialize<GroupControlMessage>(payload)!;
            var groupContext = Encoding.UTF8.GetBytes(controlMessage.GroupId);
            var groupSession = new SenderKeySession(controlMessage.SessionKey, groupContext);
            return new GroupManager(groupSession, controlMessage.GroupId);
        }

        public void ProcessRekeyMessage(DoubleRatchetSession sessionToCreator, RatchetMessage rekeyMessage)
        {
            var payload = sessionToCreator.Decrypt(rekeyMessage);
            var controlMessage = JsonSerializer.Deserialize<GroupControlMessage>(payload)!;
            var groupContext = Encoding.UTF8.GetBytes(controlMessage.GroupId);
            GroupSession = new SenderKeySession(controlMessage.SessionKey, groupContext);
            GroupId = controlMessage.GroupId;
        }
    }
}
