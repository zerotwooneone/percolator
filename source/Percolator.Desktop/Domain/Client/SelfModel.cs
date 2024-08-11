using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Domain.Client;

public class SelfModel 
{
    private readonly ILogger<SelfModel> _logger;
    public Guid IdentitySuffix { get; }
    public RSA Identity { get; }
    public ReactiveProperty<string> PreferredNickname { get; }

    public SelfModel(Guid identitySuffix, RSA identity, string preferredNickname, ILogger<SelfModel> logger)
    {
        _logger = logger;
        IdentitySuffix = identitySuffix;
        Identity = identity;
        PreferredNickname = new ReactiveProperty<string>(preferredNickname);
    }
}