# Percolator.Contracts

This project defines the data contracts and service definitions for the Percolator network, primarily using Protocol Buffers (Protobuf) and gRPC.

## Purpose

This library serves as the single source of truth for how data and services are structured. By sharing this common project, both client and server applications can ensure they are communicating with compatible and correctly-typed interfaces.

## Generated Code Namespace

**Important**: The C# classes generated from the `.proto` files are placed in the `Percolator.Contracts.Protos` namespace. Any project consuming these contracts will need to import this namespace to access the generated message and client types.
