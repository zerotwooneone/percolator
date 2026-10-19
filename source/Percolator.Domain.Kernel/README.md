# Percolator.Domain.Kernel

## Purpose
`Percolator.Domain.Kernel` provides the shared domain kernel for the Percolator architecture. It defines the foundational tactical DDD abstractions and universal identity tokens that must be shared across autonomous domain libraries without introducing domain-to-domain coupling.

## Contributor & AI Guardrails
**DO NOT ADD CODE HERE FOR CONVENIENCE.**
- This project must NEVER reference any other project except the compile-time `Percolator.SourceGenerators` analyzer.
- This project must NEVER contain feature logic (chat, file transfer, messaging, ratchet cryptography, relay routing).
- Any proposal to add a new type here must prove that the type is required by at least two completely independent domain bounded contexts and contains zero application-specific invariants.
