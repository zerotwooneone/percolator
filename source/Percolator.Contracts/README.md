# Percolator.Contracts

This project defines the data contracts and service definitions for the Percolator network, primarily using Protocol Buffers (Protobuf) and gRPC.

## Purpose

This library serves as the single source of truth for how data and services are structured. By sharing this common project, both client and server applications can ensure they are communicating with compatible and correctly-typed interfaces.

## Generated Code Namespace

**Important**: The C# classes generated from the `.proto` files are placed in the `Percolator.Contracts.Protos` namespace. Any project consuming these contracts will need to import this namespace to access the generated message and client types.

## Protocol Design

The protocol definitions follow a few key principles:

-   **`optional` Fields**: All fields are declared as `optional`. This provides a clear way to check for the presence of a field (`Has...` for scalar types, `!= null` for message types) and supports forward and backward compatibility.
-   **Versioning**: All top-level messages should include a `version` field to facilitate future protocol upgrades.
-   **Rate-Limiting Feedback**: Response messages include an optional `retry_after_utc` timestamp. This allows the server to inform clients when they have been rate-limited and when it is safe to retry the request.

## Files

-   `manifest.proto`: Defines the structure of file system manifests.
-   `filesharing.proto`: Defines the gRPC services and messages for announcing and requesting manifests and file chunks.
