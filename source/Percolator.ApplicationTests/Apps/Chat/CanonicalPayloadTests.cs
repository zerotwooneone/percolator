using System;
using System.Linq;
using Google.Protobuf;
using NUnit.Framework;
using Percolator.Application.Apps.Chat;
using Percolator.Contracts;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class CanonicalPayloadTests
    {
        private static byte[] Bs(params byte[] x) => x;

        [Test]
        public void ForAdminOperation_Sorts_Membership_Lists_Deterministically()
        {
            var convoId = Guid.NewGuid();
            var a = ByteString.CopyFrom(convoId.ToByteArray());
            var m1 = ByteString.CopyFrom(Bs(0x02));
            var m2 = ByteString.CopyFrom(Bs(0x01));
            var m3 = ByteString.CopyFrom(Bs(0x03));

            var opId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());

            AdminOperationPayload MakePayload(ByteString[] adds, ByteString[] removes)
            {
                var p = new AdminOperationPayload
                {
                    Version = 1,
                    GroupConversationGuid = a,
                    OpId = opId
                };
                p.UpdateGroupMembership = new UpdateGroupMembershipPayload { Version = 1 };
                p.UpdateGroupMembership.MembersToAdd.AddRange(adds);
                p.UpdateGroupMembership.MembersToRemove.AddRange(removes);
                return p;
            }

            var p1 = MakePayload(new[]{ m1, m2, m3 }, new[]{ m3, m2, m1 });
            var p2 = MakePayload(new[]{ m3, m1, m2 }, new[]{ m2, m1, m3 });
            var p3 = MakePayload(new[]{ m2, m3, m1 }, new[]{ m1, m3, m2 });

            var b1 = CanonicalPayload.ForAdminOperation(p1);
            var b2 = CanonicalPayload.ForAdminOperation(p2);
            var b3 = CanonicalPayload.ForAdminOperation(p3);

            Assert.That(b1.SequenceEqual(b2));
            Assert.That(b1.SequenceEqual(b3));
        }

        [Test]
        public void ForAdminOperation_Leaves_NonMembership_Ops_Untouched()
        {
            var convoId = Guid.NewGuid();
            var p = new AdminOperationPayload
            {
                Version = 1,
                GroupConversationGuid = ByteString.CopyFrom(convoId.ToByteArray()),
                OpId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                GrantAdmin = new GrantAdmin { Version = 1, GranteePublicKey = ByteString.CopyFrom(new byte[]{0xAA}) }
            };

            var bytes = CanonicalPayload.ForAdminOperation(p);
            var direct = p.ToByteArray();
            Assert.That(bytes.SequenceEqual(direct));
        }
    }
}
