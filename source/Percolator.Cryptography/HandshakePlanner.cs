using System;

namespace Percolator.Cryptography;

public sealed class HandshakePlanner
{
    public void ValidateInvitation(HandshakeInvitation inv, ICryptoPrimitives crypto)
    {
        if (inv is null) throw new ArgumentNullException(nameof(inv));
        if (crypto is null) throw new ArgumentNullException(nameof(crypto));
        if (inv.Value.Length == 0) throw new ArgumentException("Invitation payload must not be empty.", nameof(inv));
        // Additional signature/version checks will be added in future steps.
    }

    public EstablishmentPlan PlanEstablishment(PreKeyBundle bundle)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));
        var hasOtp = bundle.OneTimePreKey is not null;
        return new EstablishmentPlan(hasOtp);
    }
}
