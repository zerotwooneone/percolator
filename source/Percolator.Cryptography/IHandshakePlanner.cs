namespace Percolator.Cryptography;

// Planner port: validates inputs and produces DTO-only plans (no secrets)
public interface IHandshakePlanner
{
    void ValidateInitiatorInvitation(HandshakeInvitation inv);
    void ValidatePreKeyBundle(PreKeyBundle bundle);

    ResponderPlan PlanResponder(PreKeyBundle bundle, RatchetIdentityKey initiatorIdKey, RatchetEphemeralKey initiatorEph);
    InitiatorPlan PlanInitiator(PreKeyBundle bundle, RatchetIdentityKey responderIdKey);
}

// DTOs have no secrets; they describe which DHs/keys are required and whether OTK is present
public sealed record InitiatorPlan(
    bool RequiresOneTimePreKey
);

public sealed record ResponderPlan(
    bool HasOneTimePreKey
);
