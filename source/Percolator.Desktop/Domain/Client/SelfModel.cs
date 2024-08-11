using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Domain.Client;

public class SelfModel 
{
    private readonly ILogger<SelfModel> _logger;
    public Guid IdentitySuffix { get; }
    public RSA Identity { get; }
    public ReactiveProperty<string> PreferredNickname { get; } = new();
    public ReactiveProperty<bool> BroadcastListen { get; } = new();
    public ReactiveProperty<bool> IntroduceListen { get; } = new();
    public ReactiveProperty<bool> AutoReplyIntroductions { get; } = new();
    public ReactiveProperty<bool> BroadcastSelf { get; } = new();

    public SelfModel(Guid identitySuffix, RSA identity, ILogger<SelfModel> logger)
    {
        _logger = logger;
        IdentitySuffix = identitySuffix;
        Identity = identity;
    }
}