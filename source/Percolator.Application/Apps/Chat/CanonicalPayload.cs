using Google.Protobuf;
using Percolator.Contracts;

namespace Percolator.Application.Apps.Chat
{
    // For now, canonicalization uses protobuf's binary encoding directly.
    // In the future, if protobuf changes or we need cross-language guarantees, replace with an explicit canonicalization.
    internal static class CanonicalPayload
    {
        public static byte[] ForAdminOperation(AdminOperationPayload payload)
        {
            // TODO: Consider using a deterministic serialization mode if needed.
            return payload.ToByteArray();
        }
    }
}
