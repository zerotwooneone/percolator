# Percolator.Simulator

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Simulator` is a standalone developer and integration-testing desktop deployable. Segregated into its own executable so the production desktop client (`Percolator.Desktop`) contains zero simulator code or mock transport hooks, it hosts multiple real, isolated Percolator node instances (peers and sovereign relays) on localhost ports within a single visual process.

- **In Scope:** Multi-node localhost `IHost` lifecycle orchestration, live inspection of handshake state machines, relay queues, and decrypted packet streams, and artificial network fault/chaos injection (latency, jitter, packet drop, disconnects).
- **Out of Scope:** Production desktop client code and duplicate mock cryptographic/serialization engines.

---

## 2. Architectural Invariants and Encapsulation

1. **True Process & Container Separation:** The production desktop client interacts with simulator-hosted personas over real loopback gRPC/TCP sockets (enabled via the `--allow-localhost` development flag) exactly as it communicates with remote WAN peers.
2. **Per-Persona Host Isolation:** Each simulated peer or relay runs inside its own isolated `IHost` with its own Kestrel listener port and isolated database instance.

---

## 3. Key Technical Design Choices

1. **Out-of-Process Segregation over In-App Simulator Hooks:**
   - *Design Choice:* Running the simulator as an independent executable eliminates `ISimulatorOutboundInterceptor` hooks, magic loopback IP reservations, and DI container bloat from the production desktop client.
   - *Rejected Alternative:* Embedding a simulator tab and mock transport interceptors inside the production client, which compromises the security boundary and distorts real socket behavior.
2. **Real Stack Execution over Mock Protocol Engines:**
   - Simulated nodes execute the real [`Percolator.Domain`](../Percolator.Domain/Design.md), [`Percolator.Application`](../Percolator.Application/Design.md), and [`Percolator.Infrastructure`](../Percolator.Infrastructure/Design.md) pipelines rather than a parallel mock engine, guaranteeing that simulator tests exercise production wire serialization, ratchet state transitions, and concurrency gates.
