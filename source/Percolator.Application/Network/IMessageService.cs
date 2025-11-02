using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IMessageService
    {
        Task<SendResult> SendMessageAsync(InternalEnvelope envelope, PeerId recipientPeerId, CancellationToken ct = default);
        Task<(SendResult Result, Percolator.Contracts.DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(InternalEnvelope envelope, PeerId recipientPeerId, CancellationToken ct = default);
    }

    public sealed class SendResult
    {
        public string Path { get; }
        public string[] AttemptedPaths { get; }
        public int Attempts { get; }
        public int Retries { get; }
        public System.Exception? LastError { get; }

        private SendResult(string path, string[] attemptedPaths, int attempts, int retries, System.Exception? lastError)
        {
            Path = path;
            AttemptedPaths = attemptedPaths;
            Attempts = attempts;
            Retries = retries;
            LastError = lastError;
        }

        public static SendResult Success(string path, string[] attemptedPaths, int attempts) => new(path, attemptedPaths, attempts, 0, null);
        public static SendResult Failure(string[] attemptedPaths, int attempts, System.Exception lastError) => new("None", attemptedPaths, attempts, 0, lastError);
    }
}
