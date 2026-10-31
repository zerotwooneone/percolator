# Platform Execution Runtime Facts

Spec: [fact-spec](../spec/fact-spec.md)

## 1. External Normative Sources

- **ISO/IEC 9899:2018 (C18 Standard)** / **ISO/IEC 14882:2020 (C++20 Standard)**: Dead-Store Elimination and As-If Optimization Rules (Clause 5.1.2.3).
- **Microsoft .NET BCL Cryptographic Operations**: `.NET API Documentation: System.Security.Cryptography.CryptographicOperations.ZeroMemory`, Microsoft Learn, .NET 6–9.
- **CLR Exception Handling Mechanics & Performance**: *Pro .NET Memory Management & CLR Internals*, Konrad Kokosa, Redgate / Microsoft Press.

## 2. Axioms and Platform Invariants

1. **`FACT-DEAD-STORE-ELIMINATION` (Compiler Dead-Store Elimination of Sensitive Buffers):** Optimizing compilers and Just-In-Time (JIT) runtime compilers automatically eliminate memory writes to local stack arrays or heap buffers if the buffer is never read again prior to scope exit, retaining plaintext cryptographic keys and secrets in volatile memory unless explicitly wiped via a volatile memory barrier or runtime-pinned primitive (`CryptographicOperations.ZeroMemory` / `SecureZeroMemory`).
2. **`FACT-CLR-EXCEPTION-HOTPATH-OVERHEAD` (Stack-Trace & Exception Unwind Latency):** In the .NET Common Language Runtime (CLR), throwing and catching an exception allocates a heap object (`Exception`), triggers an unmanaged-to-managed stack-frame walk to capture stack traces, and incurs between 1,000 to 10,000 CPU cycles of latency compared to value-type conditional branching (`DomainResult<T>`), causing throughput collapse and GC fragmentation when used for foreseeable network or wire validation failures.
