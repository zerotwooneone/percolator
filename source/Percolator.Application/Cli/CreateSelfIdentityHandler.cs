using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Identity;
using System.Security.Cryptography;
using Percolator.Identity.Model;

namespace Percolator.Application.Cli;

public class CreateSelfIdentityHandler : IRequestHandler<CreateSelfIdentityCommand, SelfId>
{
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ISelfIdentityKeysStore _keysStore;
    private readonly INetworkEnvironment _networkEnvironment;
    private readonly ILogger<CreateSelfIdentityHandler> _logger;
    private readonly IReservedPortQuery _reservedPortQuery;

    public CreateSelfIdentityHandler(
        ISelfIdentityRepository selfIdentityRepository,
        ISelfIdentityKeysStore keysStore,
        INetworkEnvironment networkEnvironment,
        ILogger<CreateSelfIdentityHandler> logger,
        IReservedPortQuery reservedPortQuery)
    {
        _selfIdentityRepository = selfIdentityRepository;
        _keysStore = keysStore;
        _networkEnvironment = networkEnvironment;
        _logger = logger;
        _reservedPortQuery = reservedPortQuery;
    }

    public async Task<SelfId> Handle(CreateSelfIdentityCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required", nameof(request.Name));
        }

        var excludedPorts = await _reservedPortQuery.GetReservedPortsAsync(cancellationToken).ConfigureAwait(false);
        // Get an available port from the network environment
        var port = await _networkEnvironment.GetAvailablePortAsync(excludedPorts,cancellationToken).ConfigureAwait(false);

        // Create domain identity and persist, capturing generated id
        var now = DateTimeOffset.UtcNow;
        var identity = new Percolator.Identity.Model.SelfIdentity(new SelfId(0), PeerId.NewId(), new ListeningPort(port));
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
