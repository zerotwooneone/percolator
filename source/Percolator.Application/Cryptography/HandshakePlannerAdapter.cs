using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

public sealed class HandshakePlannerAdapter : IHandshakePlanner
{
    private readonly IPreKeyBundleValidator _bundleValidator;

    public HandshakePlannerAdapter(IPreKeyBundleValidator bundleValidator)
    {
        _bundleValidator = bundleValidator ?? throw new ArgumentNullException(nameof(bundleValidator));
    }

    public void ValidateInitiatorInvitation(HandshakeInvitation inv)
    {
        if (inv is null || inv.ToArray() is null || inv.ToArray().Length == 0)
            throw new ArgumentException("invitation payload missing", nameof(inv));
        // Further structural validation of the invitation payload (versioning, fields) occurs at the application boundary.
    }

    public void ValidatePreKeyBundle(PreKeyBundle bundle)
    {
        _bundleValidator.Validate(bundle);
    }

    public ResponderPlan PlanResponder(PreKeyBundle bundle, RatchetIdentityKey initiatorIdKey, RatchetEphemeralKey initiatorEph)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));
        if (initiatorIdKey?.ToArray() is null || initiatorIdKey.ToArray().Length == 0) throw new ArgumentException("initiator id key missing", nameof(initiatorIdKey));
        if (initiatorEph?.ToArray() is null || initiatorEph.ToArray().Length == 0) throw new ArgumentException("initiator eph key missing", nameof(initiatorEph));
        // For now, reflect whether the remote bundle advertised an OTK; responder will supply local OTK privately.
        return new ResponderPlan(HasOneTimePreKey: bundle.OneTimePreKey is not null);
    }

    public InitiatorPlan PlanInitiator(PreKeyBundle bundle, RatchetIdentityKey responderIdKey)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));
        if (responderIdKey?.ToArray() is null || responderIdKey.ToArray().Length == 0) throw new ArgumentException("responder id key missing", nameof(responderIdKey));
        return new InitiatorPlan(RequiresOneTimePreKey: bundle.OneTimePreKey is not null);
    }
}
