using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IMessageService
    {
        Task<SendResult> SendMessageAsync(
            InternalEnvelope envelope,
            PeerId recipientPeerId,
            byte[]? recipientPublicKeyHash,
            CancellationToken ct = default);
        Task<(SendResult Result, Percolator.Contracts.DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(
            InternalEnvelope envelope,
            PeerId recipientPeerId,
            byte[]? recipientPublicKeyHash,
            CancellationToken ct = default);
    }

    public sealed class SendResult
    {
        public string Path { get; }
        public string[] AttemptedPaths { get; }
        public int Attempts { get; }
        public int Retries { get; }
        public System.Exception? LastError { get; }
        public bool Success { get; }

        private SendResult(string path, string[] attemptedPaths, int attempts, int retries, System.Exception? lastError, bool success)
        {
            Path = path;
            AttemptedPaths = attemptedPaths;
            Attempts = attempts;
            Retries = retries;
            LastError = lastError;
            Success = success;
        }

        public static SendResult CreateSuccess(string path, string[] attemptedPaths, int attempts) => new(path, attemptedPaths, attempts, 0, null, true);
        public static SendResult CreateFailure(string[] attemptedPaths, int attempts, System.Exception? lastError) => new("None", attemptedPaths, attempts, 0, lastError,false);
    }
}
