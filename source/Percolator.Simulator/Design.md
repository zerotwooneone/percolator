# Percolator.Simulator

Spec: [design-spec](../spec/design-spec.md)

> [!NOTE] Non-Regulatory Tooling Disclaimer
> `Percolator.Simulator` is an engineering developer tool, interactive "monkey testing" environment, and headless integration test harness. It is **NOT** part of the formal medical device QMS (FDA 21 CFR 820.30 / IEC 62304) software item deliverables and does not contain `SYS-*` or `DEP-*` regulatory tags.

## 1. Purpose and Layer Boundary

`Percolator.Simulator` is a standalone developer environment and integration-testing desktop deployable. Segregated into its own executable so the production desktop client (`Percolator.Desktop`) contains zero simulator code or mock transport hooks, it hosts multiple real, isolated Percolator node instances (peers and sovereign relays) on localhost or LAN ports within a single visual process or headless CI harness.

- **In Scope:** Multi-node `IHost` lifecycle orchestration, interactive Avalonia UI for manual "monkey testing", headless programmatic automation harness (`ISimulatorCluster`), direct and group chat simulation, blinded rendezvous peer discovery, multi-stream swarm file transfer (including late-joining peer convergence), unencrypted fast SQLite persistence, and artificial network fault/chaos injection (latency, jitter, packet drop, disconnects).
- **Out of Scope:** Production desktop client code, mock cryptographic math engines (must execute real `SignalCryptoEngine` / `ZkgroupCryptographyService`), and production SQLCipher database unlocking ceremonies.

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-SIMULATOR-PROCESS-SEPARATION` (True Process & Container Separation):** The production desktop client interacts with simulator-hosted personas over real loopback or LAN gRPC/TCP sockets (enabled via the `--allow-localhost` development flag) exactly as it communicates with remote WAN peers.
2. **`INV-SIMULATOR-HOST-ISOLATION` (Per-Persona Host Isolation):** Each simulated peer or relay runs inside its own isolated `IHost` with its own listener port, scoped dependency container, and isolated database instance.
3. **`INV-SIMULATOR-DUAL-MODE` (Interactive UI & Headless Harness Equivalence):** Every simulation capability (identity creation, relay hosting, group messaging, swarm transfer) must be accessible both interactively through the Avalonia UI and programmatically through the headless `ISimulatorCluster` test harness.
4. **`INV-SIMULATOR-SWARM-CONVERGENCE` (Multi-Stream Bit-for-Bit Swarm Convergence):** All file transfer swarm simulations (including topologies with late-joining peers) must execute real chunk hashing, Merkle tree verification, and parallel stream pipelines, converging on 100% bit-for-bit payload integrity.

---

## 3. Key Technical Design Choices

1. **`DEC-OUT-OF-PROCESS-SIMULATOR` (Out-of-Process Segregation over In-App Simulator Hooks):**
   - *Design Choice:* Running the simulator as an independent executable eliminates `ISimulatorOutboundInterceptor` hooks, magic loopback IP reservations, and DI container bloat from the production desktop client.
   - *Rejected Alternative:* Embedding a simulator tab and mock transport interceptors inside the production client, which compromises the security boundary and distorts real socket behavior.
2. **`DEC-REAL-STACK-EXECUTION` (Real Stack Execution over Mock Protocol Engines):**
   - *Design Choice:* Simulated nodes execute the real [`Percolator.Domain`](../Percolator.Domain/Design.md), [`Percolator.Application`](../Percolator.Application/Design.md), and [`Percolator.Infrastructure`](../Percolator.Infrastructure/Design.md) pipelines rather than a parallel mock engine, guaranteeing that simulator tests exercise production wire serialization, ratchet state transitions, and concurrency gates.
   - *Rejected Alternative:* Writing simulated mock transport handlers that bypass real Protobuf framing and crypto engines.
3. **`DEC-SIMULATOR-FAST-SQLITE-PERSISTENCE` (Unencrypted SQLite for Simulated Personas):**
   - *Design Choice:* Replaces SQLCipher AES-256 page-level encryption with unencrypted SQLite (`Data Source=:memory:` or temp `.db` files per persona), bypassing PBKDF2/Argon2 passphrase derivation to boot simulated nodes in milliseconds while executing production EF Core relational schemas and CQRS projections.
   - *Rejected Alternative:* Requiring full SQLCipher passphrase derivation and envelope unpacking for every simulated node, which severely slows down CI integration suites and monkey testing.
4. **`DEC-SIMULATOR-BINDING-FLEXIBILITY` (Localhost Default with Configurable External Interface Binding):**
   - *Design Choice:* Binds simulated Kestrel and TCP stream listeners to localhost (`127.0.0.1`) by default for local machine isolation, while supporting optional binding to `0.0.0.0` or specific IP addresses to allow inter-process and inter-machine testing with real `Percolator.Desktop` instances across LANs.
   - *Rejected Alternative:* Hardcoding `127.0.0.1` only, which blocks testing multi-machine or VM-to-host scenarios.
5. **`DEC-SIMULATOR-HEADLESS-HARNESS` (Embedded Programmatic Cluster Fixture for Automated Integration Suites):**
   - *Design Choice:* Exposes `ISimulatorCluster` as a public C# test fixture and headless CLI runner (`--headless --scenario <name>`), allowing automated integration test suites (`Percolator.ApplicationIntegrationTests`) and CI scripts to programmatically orchestrate complex network topologies without launching the GUI.
   - *Rejected Alternative:* Forcing all integration tests to run through GUI automation or requiring manual human button clicks.
