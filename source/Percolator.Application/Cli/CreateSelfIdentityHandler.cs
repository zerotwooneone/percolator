using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;
using System.Security.Cryptography;

namespace Percolator.Application.Cli;

public class CreateSelfIdentityHandler : IRequestHandler<CreateSelfIdentityCommand, SelfId>
{
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ISelfIdentityKeysStore _keysStore;
    private readonly ILogger<CreateSelfIdentityHandler> _logger;

    public CreateSelfIdentityHandler(
        ISelfIdentityRepository selfIdentityRepository,
        ISelfIdentityKeysStore keysStore,
        ILogger<CreateSelfIdentityHandler> logger)
    {
        _selfIdentityRepository = selfIdentityRepository;
        _keysStore = keysStore;
        _logger = logger;
    }

    public async Task<SelfId> Handle(CreateSelfIdentityCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required", nameof(request.Name));
        }

        // Create domain identity and persist, capturing generated id
        var now = DateTimeOffset.UtcNow;
        var identity = new Percolator.Identity.Model.SelfIdentity(new SelfId(0), PeerId.NewId());
        identity.SetDisplayName(request.Name);
        identity.TouchLastUsed(now);
        var newId = await _selfIdentityRepository.CreateAsync(identity, cancellationToken).ConfigureAwait(false);
        
        // Generate and persist X3DH keys for the new self identity
        var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = new X3dhKeys(ikSigning, spk);
        await _keysStore.SaveAsync(newId, keys, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created keys for identity '{Name}' (SelfIdentityId={Id}, PublicKey={PublicKey})", request.Name, newId, Convert.ToBase64String(keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()));

        return newId;
    }
}
