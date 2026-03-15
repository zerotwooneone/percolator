using Google.Protobuf;
using Google.Protobuf.Collections;
using Percolator.Contracts;

namespace Percolator.Application.Apps.Chat
{
    // Canonicalization strategy:
    // - Our AdminOperationPayload does not use protobuf map fields; protobuf binary encoding is stable
    //   for singular fields given the same ordering of repeated collections.
    // - We normalize (sort) repeated byte fields within UpdateGroupMembershipPayload to ensure a deterministic order
    //   regardless of caller order, then serialize with protobuf's binary encoding.
    internal static class CanonicalPayload
    {
        public static byte[] ForAdminOperation(AdminOperationPayload payload)
        {
            switch (payload.OperationCase)
            {
                case AdminOperationPayload.OperationOneofCase.GrantAdmin:
                    return payload.ToByteArray();
                case AdminOperationPayload.OperationOneofCase.RevokeAdmin:
                    return payload.ToByteArray();
                case AdminOperationPayload.OperationOneofCase.UpdateGroupInfo:
                    return payload.ToByteArray();
                case AdminOperationPayload.OperationOneofCase.UpdateGroupMembership:
                    // Normalize member lists by sorting lexicographically
                    if (payload.UpdateGroupMembership is null)
                    {
                        return payload.ToByteArray();
                    }
                    // Create a shallow copy we can normalize without mutating caller payload
                    var copy = new AdminOperationPayload
                    {
                        Version = payload.Version,
                        GroupConversationGuid = payload.GroupConversationGuid,
                        OpId = payload.OpId,
                        SentTimestampUtc = payload.SentTimestampUtc,
                        AdminSequenceNumber = payload.AdminSequenceNumber
                    };
                    var norm = new UpdateGroupMembershipPayload
                    {
                        Version = payload.UpdateGroupMembership.Version,
                        LeaveGroup = payload.UpdateGroupMembership.LeaveGroup
                    };

                    void AddSorted(RepeatedField<ByteString> source, RepeatedField<ByteString> dest)
                    {
                        foreach (var bs in source.OrderBy(b => b.ToByteArray(), ByteArrayComparer.Instance))
                        {
                            dest.Add(bs);
                        }
                    }

                    AddSorted(payload.UpdateGroupMembership.MembersToAdd, norm.MembersToAdd);
                    AddSorted(payload.UpdateGroupMembership.MembersToRemove, norm.MembersToRemove);
                    copy.UpdateGroupMembership = norm;
                    return copy.ToByteArray();
                case AdminOperationPayload.OperationOneofCase.None:
                default:
                    return payload.ToByteArray();
            }
        }

        private sealed class ByteArrayComparer : System.Collections.Generic.IComparer<byte[]>
        {
            public static readonly ByteArrayComparer Instance = new ByteArrayComparer();
            public int Compare(byte[]? x, byte[]? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;
                var len = Math.Min(x.Length, y.Length);
                for (int i = 0; i < len; i++)
                {
                    int c = x[i].CompareTo(y[i]);
                    if (c != 0) return c;
                }
                return x.Length.CompareTo(y.Length);
            }
        }
    }
}
