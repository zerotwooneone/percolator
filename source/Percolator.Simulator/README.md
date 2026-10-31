# Percolator.Simulator

`Percolator.Simulator` is a standalone, cross-platform developer tool and testing runtime built with **Avalonia UI**, **FluentAvalonia**, and **CommunityToolkit.Mvvm**.

It simulates multi-peer and multi-relay network topologies on localhost, enabling rapid development, automated integration testing, and interactive protocol debugging of the `Percolator.Desktop` client without requiring multiple physical machines or live WAN deployments.

For full architectural principles, simulation runtime mechanics, and process isolation design, see the project [Design Document](Design.md).

---

## Architectural Principles

1. **True Process Separation**: Runs as an independent process. The production desktop application (`Percolator.Desktop`) contains **zero simulator code**, mock interceptors, or artificial loopback hacks.
2. **Real Infrastructure Stacks**: Instead of redundant mock networking engines, the simulator instantiates genuine Kestrel gRPC listeners, SQLite databases, and transport adapters from `Percolator.Infrastructure` bound to distinct localhost ports (`127.0.0.1:5100`–`5299`).
3. **Multi-Node Hosting**: A single running simulator instance hosts multiple independent cryptographic identities (e.g. Alice, Bob, Relay 1, Relay 2), each appearing as an independent remote peer to `Percolator.Desktop`.
4. **Fault & Chaos Injection**: Configurable artificial latency (jitter via `ISimulatorDelay`), packet drop rates, and abrupt socket disconnects to verify client retry and relay failover mechanisms.

---

## Desktop Client Interoperability

To connect `Percolator.Desktop` to simulated nodes on localhost, enable the development loopback flag:

```json
{
  "Development": {
    "AllowLocalhostPeers": true
  }
}
```

Or pass the CLI argument `--allow-localhost`.
