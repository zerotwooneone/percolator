# Percolator.Simulator Implementation Plan

## 1. Executive Summary

`Percolator.Simulator` is a standalone, cross-platform developer tool and testing runtime built with **Avalonia UI**, **FluentAvalonia**, and **CommunityToolkit.Mvvm**. 

Its purpose is to simulate multi-peer, multi-relay network topologies on localhost, enabling rapid development, automated integration testing, and interactive debugging of the `Percolator.Desktop` client without requiring multiple physical machines or real WAN deployments.

### Core Architectural Principles:
1. **True Process Separation**:
   - The simulator runs as its own independent process.
   - The production desktop application (`Percolator.Desktop`) remains completely clean, containing **zero simulator code**, mock interceptors, or artificial loopback hacks.
2. **Reuse Real Infrastructure (Zero Mock Engine Bloat)**:
   - The simulator avoids the massive complexity of building a redundant mock networking engine.
   - It directly instantiates real infrastructure from `Percolator.Network`, `Percolator.Infrastructure2`, and real Kestrel/gRPC endpoints bound to distinct local ports (e.g. `127.0.0.1:5101`, `127.0.0.1:5102`, `127.0.0.1:5200`).
3. **Zero Encryption & Plaintext Transparency**:
   - For simulated test personas, cryptographic envelopes can be optionally bypassed or decrypted in-memory, providing full plaintext visibility into message bodies, routing headers, and handshake payloads.
4. **Multi-Peer & Multi-Relay Hosting in One Process**:
   - A single running simulator instance hosts multiple independent cryptographic identities (e.g. "Peer Alice", "Peer Bob", "Relay Node 1", "Relay Node 2").
   - To the external `Percolator.Desktop` client, each simulated persona appears as an independent remote peer.
5. **Fault & Chaos Injection**:
   - Configurable artificial latency (e.g. 50ms–500ms jitter via `ISimulatorDelay`).
   - Simulated packet drop rates and abrupt socket disconnects to verify client retry and relay failover mechanisms.

---

## 2. Lessons Learned from `Desktop.Wpf` Simulator

The scan of `Desktop.Wpf/Features/Simulator` identified patterns to retain and pitfalls to eliminate:

### 2.1 What Was Wrong in `Desktop.Wpf`
* **In-Process Parasite**: The simulator was embedded directly inside the production WPF app, injecting `ISimulatorOutboundInterceptor` into production transport pipelines and reserving magic IP ranges (`127.77.*`).
* **Main Container Pollution**: Simulator repositories, delay services, and simulated key factories were registered into the main DI container, bloating startup time and blurring production boundaries.

### 2.2 What Worked Well & Must Be Modernized
* **Handshake State Machine Inspector** ([`SimulatedHandshakeStateMachineCardViewModel`](file:///C:/Users/squir/source/repos/percolator/source/Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs)):
  - Visualized the multi-step handshake states (Invitation Sent, Ephemeral Prekey Exchanged, Session Established, Expired).
* **Relay Queue & Backlog Panel** ([`SimulatedRelayQueuePanelViewModel`](file:///C:/Users/squir/source/repos/percolator/source/Desktop.Wpf/Features/Simulator/SimulatedRelayQueuePanelViewModel.cs)):
  - Tracked messages enqueued at intermediate relay nodes awaiting delivery or polling by downstream peers.
* **Peer Registry & Live Telemetry** ([`SimulatorPeersTabViewModel`](file:///C:/Users/squir/source/repos/percolator/source/Desktop.Wpf/Features/Simulator/SimulatorPeersTabViewModel.cs)):
  - Live list of simulated nodes with toggleable online/offline states, latency dials, and message traffic counters.

---

## 3. Desktop Client Interoperability

The production desktop application (`Percolator.Desktop`) remains completely ignorant of simulator internals. To interact with the simulator:
* `Percolator.Desktop` accepts a development setting:
  - Configuration: `Development:AllowLocalhostPeers: true` (in `appsettings.Development.json`) or CLI flag `--allow-localhost`.
  - When enabled, the desktop client permits connections to loopback addresses (`127.0.0.1`, `::1`), which are normally blocked by production WAN stealth policies.
* The desktop client connects to the simulator's real gRPC/TCP endpoints exactly as it would connect to any real peer on the internet.

---

## 4. Technology Stack & Component Architecture

| Layer | Component | Description |
|---|---|---|
| **Presentation Framework** | Avalonia UI 11.2 | Hardware-accelerated, cross-platform UI engine |
| **Theme & Controls** | FluentAvaloniaUI 2.1 | Modern Windows 11 controls, NavigationView, CommandBars |
| **MVVM Tooling** | CommunityToolkit.Mvvm 8.4 | Source-generated `[ObservableProperty]`, `[RelayCommand]` |
| **Reactive Pipelines** | R3 (v1.3) + ObservableCollections (v3.3) | Telemetry monitoring and real-time packet feeds |
| **Network Engine** | Real `Percolator.Network` & `Percolator.Infrastructure2` | Real gRPC/Kestrel transport listeners bound to localhost ports |
| **Hosting & DI** | `Microsoft.Extensions.Hosting` | Clean lifecycle management for simulated node hosts |

---

## 5. UI Architecture & Directory Structure

```
Percolator.Simulator/
├── Assets/                        # Platform icons and branding
├── Common/
│   ├── Controls/                  # MetricGauges, TrafficIndicator, StatusBadges
│   └── Converters/                # Value converters (PacketSize, LatencyToBrush)
├── Features/
│   ├── Shell/                     # Simulator main window & tabbed shell
│   │   ├── ShellViewModel.cs
│   │   └── ShellView.axaml
│   ├── Peers/                     # Simulated Peer Manager
│   │   ├── State/                 # SimulatedPeerRegistry
│   │   ├── ViewModels/            # SimulatedPeersViewModel, PeerCardViewModel
│   │   └── Views/                 # SimulatedPeersView.axaml
│   ├── Relays/                    # Simulated Relay Hosts & Queues
│   │   ├── State/                 # SimulatedRelayEngine
│   │   ├── ViewModels/            # RelayQueueViewModel, RelayHostCardViewModel
│   │   └── Views/                 # RelayQueueView.axaml
│   ├── Handshakes/                # Handshake State Machine Monitor
│   │   ├── ViewModels/            # HandshakeMonitorViewModel, HandshakeStepViewModel
│   │   └── Views/                 # HandshakeMonitorView.axaml
│   ├── Traffic/                   # Live Packet & Message Stream Inspector
│   │   ├── ViewModels/            # PacketInspectorViewModel
│   │   └── Views/                 # PacketInspectorView.axaml
│   └── Chaos/                     # Latency & Network Condition Controls
│       ├── ViewModels/            # ChaosControlViewModel (latency sliders, drop rates)
│       └── Views/                 # ChaosControlView.axaml
├── Runtime/                       # Real Infrastructure Node Orchestrator
│   ├── SimulatedNodeHost.cs       # Spins up real Kestrel gRPC listener per persona
│   ├── SimulatedNodeRegistry.cs   # Port allocator & identity manager
│   └── Ingress/                   # Plaintext message sniffer and telemetry tap
├── ViewModels/
│   ├── ViewModelBase.cs           # ObservableObject + DisposableBag
│   └── MainWindowViewModel.cs
├── Views/
│   ├── MainWindow.axaml
│   └── MainWindow.axaml.cs
├── App.axaml
├── App.axaml.cs
├── Program.cs
└── Percolator.Simulator.csproj
```

---

## 6. Implementation Roadmap & Milestones

### Milestone 1: App Shell & Node Runtime Host (Sprint 1)
- [x] Scaffold `Percolator.Simulator` with Avalonia, FluentAvalonia, CommunityToolkit.Mvvm, R3.
- [x] Add references to real infrastructure (`Percolator.Network`, `Percolator.Application`, `Percolator.Domain`).
- [ ] Implement `SimulatedNodeHost` capable of dynamically starting/stopping real Kestrel gRPC listeners on allocated localhost ports (`5100-5199`).
- [ ] Configure `NavigationView` shell with Peers, Relays, Handshakes, Traffic, and Chaos tabs.

### Milestone 2: Multi-Peer Manager & Identity Spawner (Sprint 2)
- [ ] UI for spawning new simulated peers with customizable personas ("Alice", "Bob", "Mallory").
- [ ] Auto-generate Ed25519/X25519 identity keypairs via `Percolator.Domain`.
- [ ] Display QR codes and copyable connection invite tokens for instant pasting into `Percolator.Desktop`.
- [ ] Peer controls: Toggle Online/Offline, Pause processing, Reset sessions.

### Milestone 3: Relay Node Simulation & Queue Inspector (Sprint 3)
- [ ] Configure simulated nodes to act as relay hosts (`Percolator.Network` relay protocols).
- [ ] Live queue visualizer: inspect enqueued opaque messages waiting for downstream polling.
- [ ] Manual & automated relay delivery trigger controls.

### Milestone 4: Handshake State Machine Visualizer (Sprint 4)
- [ ] Subscribe to handshake protocol events from real infrastructure.
- [ ] Visual state machine card rendering current phase (Init, PrekeyExchange, Completed, Expired).
- [ ] Step-by-step handshake debugging mode (pause handshake at step N for protocol inspection).

### Milestone 5: Plaintext Traffic Inspector & Chaos Dial (Sprint 5)
- [ ] Live packet feed displaying message IDs, timestamps, routes (Direct vs. Relayed), and payload size.
- [ ] Plaintext message viewer (inspect decrypted text payloads in real time).
- [ ] Chaos controls:
  - Latency slider (0ms – 1000ms delay injection).
  - Packet drop simulator (0% – 50% simulated dropped frames).
  - Jitter dial.
