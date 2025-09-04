using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Identity;
using System.Security.Cryptography;

namespace Percolator.Application.Cli;

public class CreateSelfIdentityHandler : IRequestHandler<CreateSelfIdentityCommand, int>
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

    public async Task<int> Handle(CreateSelfIdentityCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required", nameof(request.Name));
        }

        var peerId = request.PeerId ?? Guid.NewGuid();
        var id = await _selfIdentityRepository.CreateAsync(peerId, request.Name);

        // Generate and persist X3DH keys for the new self identity
        var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ikAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = new X3dhKeys(ikSigning, ikAgreement, spk);
        await _keysStore.SaveAsync(id, keys, cancellationToken);
        _logger.LogInformation("Created keys for identity '{Name}' (SelfIdentityId={Id}, PeerId={PeerId}, PublicKey={PublicKey})", request.Name, id, peerId, Convert.ToBase64String(keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()));

        return id;
    }
}
