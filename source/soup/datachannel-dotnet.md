# DataChannelDotnet SOUP Specification

Spec: [soup-spec](../spec/soup-spec.md)

## 1. Component Identification and Provenance

| Property | Value |
| :--- | :--- |
| **Component Name** | `DataChannelDotnet` (Managed C# Wrapper) and bundled native `libdatachannel` (`datachannel.dll` / `datachannel.so` / `datachannel.dylib`, embedding `libjuice`, `usrsctp`, and OpenSSL 3 `libssl-3` / `libcrypto-3`) |
| **Supplier / Maintainer** | `ZetrocDev` (`https://github.com/zetrocdev/datachanneldotnet`) wrapping upstream `paullouisageneau/libdatachannel`, `paullouisageneau/libjuice`, `sctplab/usrsctp`, and OpenSSL 3 |
| **Safety Classification & Rationale** | **Class C** (IEC 62304 Clause 4.3).<br/>*Rationale:* The component manages unmanaged C/C++ network sockets, UDP ICE/STUN NAT traversal, DTLS certificate handshakes, and SCTP transport framing for peer-to-peer bulk file transfer. An unmitigated native memory-safety defect, DTLS certificate fingerprint verification bypass, or orphaned native socket handle can directly result in arbitrary memory corruption, unauthorized transport interception, IP/presence leakage during idle or locked states, or host process termination. |
| **Approved Version & Hash** | NuGet `DataChannelDotnet` `1.3.1` (Published `2026-01-20T20:31:49Z`; Package Size: `8,916,748` bytes)<br/>NuGet Package SHA-512 (base64): `soFcye/6tSQlOIMy8fdhaY1AbPtf3TqblSxNwQ1kifqQEzewhSZgfqLVscpGqhiUtHHJLhsDmNw/OWcxkUd1ww==`<br/>Managed Assembly: `lib/net9.0/DataChannelDotnet.dll`<br/>Native Binaries: `runtimes/win-x64/native/datachannel.dll`, `libcrypto-3-x64.dll`, `libssl-3-x64.dll`; `runtimes/win-x86/native/datachannel.dll`, `libcrypto-3.dll`, `libssl-3.dll`; `runtimes/linux-x64/native/datachannel.so`; `runtimes/osx-x64/native/datachannel.dylib` |
| **License Model** | MIT (`DataChannelDotnet` managed wrapper); MPL-2.0 (`libdatachannel`, `libjuice`); BSD-3-Clause (`usrsctp`); Apache-2.0 (`OpenSSL 3`). Third-party attribution notices and MPL-2.0 source availability notices apply upon binary redistribution. |
| **Delivery & Linking Format** | Pre-compiled NuGet package containing managed assembly (`net9.0`, forward-compatible with `net10.0`) + platform-specific unmanaged dynamic libraries loaded via P/Invoke C-ABI (`runtimes/{RID}/native/`). |
| **Upstream Repository URL** | `https://github.com/zetrocdev/datachanneldotnet` (upstream native engines: `https://github.com/paullouisageneau/libdatachannel` and `https://github.com/paullouisageneau/libjuice`) |

## 2. Hardware and Software Prerequisites

- **Supported Operating Systems & Runtimes:** Windows 10/11 64-bit (`win-x64`) with Microsoft Visual C++ 2015–2022 Redistributable (`vcruntime140.dll` / Windows Universal CRT), Linux 64-bit (`linux-x64`, glibc 2.35+), and macOS 64-bit (`osx-x64`); Microsoft .NET 9.0 / .NET 10.0 Runtime.
- **Hardware Architecture:** x86-64 (AMD64) primary target architecture (with `win-x86` bundled for Windows compatibility), UDP socket support for ICE/STUN connectivity checks, and bounded native heap capacity for SCTP send/receive buffers.
- **Native / Unmanaged Prerequisites:** Bundled native libraries (`datachannel.dll`, `libssl-3-x64.dll`, `libcrypto-3-x64.dll` on Windows; `datachannel.so` on Linux; `datachannel.dylib` on macOS) deployed under the `runtimes/{RID}/native/` convention and resolved via standard .NET `DllImport` probing; ephemeral OS UDP port allocation strictly during active file transfer or unlocked seeding sessions.

## 3. Specified Functional and Performance Capabilities

### 3.1 Active Functional & Capacity Envelope

1. **`SOUP-DATACHANNEL-ICE-STUN` (Ephemeral ICE Candidate Gathering & UDP NAT Traversal via `libjuice`):** `[Verify: Integration]`
   The SOUP component performs on-demand UDP host and server-reflexive ICE candidate gathering and peer-to-peer NAT hole-punching connectivity checks using exclusively Percolator Exchange RFC 5389 UDP STUN endpoints (or LAN host candidates only when no Exchange STUN endpoint is configured), releasing all UDP sockets immediately upon session stop, cancel, pause, or vault lock.
   - *Capacity & Concurrency Constraints:* Peer connections are bounded per active transfer session and instantiated exclusively during user-consented Send/Accept or unlocked seeding; unmanaged event callbacks execute on internal `libdatachannel` worker threads and must never block or invoke synchronous native disposal from within a callback.
   - *Architectural Quarantine Adapter:* Quarantined WebRTC transfer transport adapter in `Percolator.Infrastructure`.

2. **`SOUP-DATACHANNEL-DTLS-PINNED` (DTLS Transport Encryption with E2E-Pinned Certificate Fingerprints):** `[Verify: Integration]`
   The SOUP component establishes standard DTLS transport security over the negotiated ICE channel using a transfer-scoped ephemeral ECDSA P-256 certificate/keypair and exposes the remote peer's SDP-declared SHA-256 certificate fingerprint for strict constant-time verification against the out-of-band fingerprint pinned over Double Ratchet or Group V2 SenderKey before any application data frame is accepted.
   - *Capacity & Concurrency Constraints:* Rejects any DTLS session whose remote fingerprint mismatches the E2E-pinned SHA-256 digest or fails to complete handshake within the configured connection deadline.
   - *Architectural Quarantine Adapter:* Quarantined WebRTC transfer transport adapter in `Percolator.Infrastructure`.

3. **`SOUP-DATACHANNEL-SCTP-STREAMS` (Reliable Binary SCTP DataChannel Framing & Backpressure):** `[Verify: Simulation]`
   The SOUP component transports binary file-transfer protocol frames over reliable SCTP DataChannels with explicit send-buffer backpressure (`bufferedAmount` / `bufferedAmountLowThreshold`) and bounded frame ceilings so multi-gigabyte transfers maintain bounded $O(1)$ managed and native heap usage.
   - *Capacity & Concurrency Constraints:* Binary framing only; outbound producers suspend whenever native queued bytes reach the configured backpressure ceiling and resume upon buffered-amount-low notifications; all native peer and channel handles are deterministically closed on teardown.
   - *Architectural Quarantine Adapter:* Quarantined WebRTC transfer transport adapter in `Percolator.Infrastructure`.

### 3.2 Unused and Prohibited Capabilities (FDA OTS Guidance §V.A.2)

1. **Media Tracks, RTP, RTCP, and SRTP Subsystems:**
   - *Prohibited Functions:* Audio/video media tracks, RTP packetizers/depacketizers, RTCP feedback handlers, and `libsrtp` cipher pipelines exposed by `libdatachannel` and `DataChannelDotnet`.
   - *Quarantine Enforcement:* Excluded completely from the infrastructure adapter boundary; peer connections negotiate strictly SCTP application data channels (`m=application`) and reject any remote SDP containing audio or video media descriptions prior to applying remote descriptions.
2. **Built-In WebSocket Signaling & Third-Party Public STUN/TURN Servers:**
   - *Prohibited Functions:* `libdatachannel` WebSocket server/client APIs, unencrypted signaling endpoints, and hardcoded external public STUN/TURN infrastructure.
   - *Quarantine Enforcement:* All SDP Offer/Answer and trickle ICE candidate signaling travels exclusively inside Percolator's end-to-end encrypted Double Ratchet or Group V2 SenderKey channels; ICE configuration accepts only linked Percolator Exchange STUN endpoints (zero third-party servers).
3. **Direct Raw C-ABI Bindings (`DataChannel.Bindings`) Outside Quarantine:**
   - *Prohibited Functions:* ClangSharp-generated P/Invoke entrypoints and raw unmanaged peer/channel integer handles in `DataChannel.Bindings`.
   - *Quarantine Enforcement:* `Percolator.Domain`, `Percolator.Application`, and `Percolator.Apps.FileTransfer` hold zero package or assembly references to `DataChannelDotnet`; all transport operations cross strictly through abstract domain/plugin port interfaces implemented in `Percolator.Infrastructure`.

## 4. Known Anomalies and Cybersecurity Evaluation

1. **`SOUP-DATACHANNEL-ANO-CALLBACK-REENTRANCY` (Native Thread-Pool Deadlock on Reentrant Disposal):**
   - **External Reference / Defect:** `libdatachannel` callback thread-affinity constraint when closing or deleting a peer connection or data channel synchronously inside a native event callback.
   - **Supplier Summary:** Invoking native peer or data-channel teardown synchronously from a `libdatachannel` internal callback thread can deadlock the native worker thread pool or induce use-after-free faults.
   - **Relevance & Reachability:** Reachable whenever handshake errors, DTLS fingerprint mismatches, or remote closes trigger immediate teardown from native event handlers.
   - **Safety & Security Impact:** Process deadlock or native crash leaving unclosed sockets or desynchronized transfer state ([`HAZ-CRASH-STATE-DESYNC`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards), [`HAZ-POST-LOCK-MEMORY-RESIDUE`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** The quarantine adapter never invokes native close/dispose synchronously on a native callback thread; callbacks post state notifications into bounded managed channels and execute native teardown asynchronously on .NET thread-pool threads.

2. **`SOUP-DATACHANNEL-ANO-CALLBACK-GC` (Premature Managed Delegate Collection Across P/Invoke):**
   - **External Reference / Defect:** CLR garbage-collection delegate lifetime hazard across `DataChannelDotnet` P/Invoke callbacks.
   - **Supplier Summary:** If managed delegates registered as native event callbacks are collected by the CLR GC while `libdatachannel` retains unmanaged function pointers, subsequent network events trigger an unmanaged Access Violation.
   - **Relevance & Reachability:** Reachable during long-running multi-gigabyte transfers or background unlocked seeding sessions.
   - **Safety & Security Impact:** Abrupt host process termination ([`HAZ-CRASH-STATE-DESYNC`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** The quarantine adapter retains strong managed references to all active peer-connection wrappers, data channels, and callback delegates until explicit asynchronous native teardown completes.

3. **`SOUP-DATACHANNEL-ANO-SDP-IP-CANDIDATE-EXPOSURE` (Direct Host and Reflexive IP Disclosure via ICE Candidates):**
   - **External Reference / Defect:** Inherent RFC 8445 ICE candidate advertisement behavior in `libjuice`.
   - **Supplier Summary:** Establishing direct peer-to-peer UDP connectivity requires gathering and transmitting local host IP addresses and STUN-discovered public server-reflexive `IP:Port` candidates to the remote peer.
   - **Relevance & Reachability:** Reachable on every direct peer-to-peer file transfer session.
   - **Safety & Security Impact:** Discloses the user's direct network address to participating transfer peers ([`HAZ-DIRECT-TRANSFER-IP-DISCLOSURE`](../RiskRegister.md#1-cryptographic-identity-and-privacy-hazards)) and binds ephemeral UDP sockets for the transfer's active lifetime ([`HAZ-DIRECT-TRANSFER-LISTENER-EXPOSURE`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** ICE agents are instantiated exclusively after explicit user Send/Accept confirmation following an unavoidable IP-disclosure notice, restricted strictly to approved contacts or authorized Group V2 swarm members, transmitted solely inside E2E Double Ratchet / Group V2 envelopes (zero Exchange visibility of SDP or LAN IPs), and torn down immediately on transfer stop, group member eviction, or vault lock.

4. **`SOUP-DATACHANNEL-ANO-UNBOUNDED-SEND-QUEUE` (Native SCTP Send Buffer Growth Under Fast Producers):**
   - **External Reference / Defect:** Unthrottled binary sends against `usrsctp` / `libdatachannel` when local disk read throughput exceeds peer WAN upload bandwidth.
   - **Supplier Summary:** Continuously enqueueing chunks without observing `BufferedAmount` causes unbounded unmanaged heap accumulation in the native SCTP send buffer.
   - **Relevance & Reachability:** Reachable during every large file upload across asymmetric consumer broadband links.
   - **Safety & Security Impact:** Local memory exhaustion and out-of-memory process failure ([`HAZ-INGRESS-RESOURCE-EXHAUSTION`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** The quarantine adapter enforces explicit SCTP send-buffer high/low watermarks (`bufferedAmount` / `bufferedAmountLowThreshold`) and pooled fixed-size frames, pausing chunk reads until buffered bytes drop below threshold.

5. **`SOUP-DATACHANNEL-ANO-NATIVE-MEMORY-RESIDUE` (Managed Transfer Key Buffer Residue Prior to Native Initialization):**
   - **External Reference / Defect:** CLR heap retention of transient DER/PEM ephemeral DTLS private key buffers passed into native OpenSSL 3 / `libdatachannel` initialization ([`FACT-DEAD-STORE-ELIMINATION`](../facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - **Supplier Summary:** Managed byte buffers holding transfer-scoped ephemeral DTLS private keys remain on the CLR heap until overwritten unless explicitly erased.
   - **Relevance & Reachability:** Reachable whenever a transfer session creates or loads its vault-persisted ephemeral DTLS certificate/keypair.
   - **Safety & Security Impact:** Forensic exposure of transient transfer key material after session lock ([`HAZ-POST-LOCK-MEMORY-RESIDUE`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** Ephemeral private key buffers are erased immediately after native initialization via `CryptographicOperations.ZeroMemory`, peer connections are destroyed immediately on vault lock, and vault-persisted transfer keys are deleted upon transfer removal.

## 5. Maintenance and Obsolescence Plan

- **Vulnerability Monitoring Cadence:** The engineering team reviews upstream advisories for `zetrocdev/datachanneldotnet`, `paullouisageneau/libdatachannel`, `paullouisageneau/libjuice`, `sctplab/usrsctp`, and bundled OpenSSL 3 quarterly alongside automated `dotnet list package --vulnerable` and GitHub Dependabot checks.
- **Patch Qualification & Regression Verification:** Before any updated `DataChannelDotnet` NuGet package version is adopted, the pinned package version and SHA-512 digest in Section 1 must be updated and 100% pass rate verified across `Percolator.Infrastructure.Tests`, `Percolator.Apps.FileTransfer.Tests`, and `Percolator.ApplicationIntegrationTests` (covering 1:1 direct DTLS transfer, 3-node Group V2 offline-author swarm handoff, DTLS fingerprint mismatch rejection, and vault-lock socket teardown).
- **Obsolescence & Deprecation Contingency:** All `DataChannelDotnet` functionality is quarantined strictly behind abstract transport port interfaces in `Percolator.Infrastructure`. If upstream maintenance ceases or an unpatched native vulnerability arises, the infrastructure adapter can be replaced with an alternative ICE/QUIC or WebRTC transport implementation (e.g., `SIPSorcery`, `msquic` + ICE, or a Rust `iroh`/`quinn` C-ABI shim) without modifying `Percolator.Domain`, `Percolator.Application`, or `Percolator.Apps.FileTransfer`.
