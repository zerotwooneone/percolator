# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Key Projects

*   **`Pecolator.Cryptography`**: A high-performance, secure cryptography library providing implementations of advanced protocols for secure messaging.
*   **`Percolator.CryptographyTests`**: A comprehensive test suite for the cryptography library, ensuring its correctness and security through rigorous unit testing.

## Overview

The primary component of this solution is the `Pecolator.Cryptography` library, which implements a full end-to-end secure messaging system based on the Signal Protocol. This includes the X3DH key agreement protocol and the Double Ratchet algorithm.

## Guidance for AI Assistants

*   **Project Goal**: The main objective of this solution is to provide a robust, secure, and well-tested implementation of modern cryptographic protocols.
*   **Key Components**: The core logic is in `Pecolator.Cryptography`. All changes to this library must be accompanied by corresponding tests in `Percolator.CryptographyTests`.
*   **Development Philosophy**: Follow a test-driven development (TDD) approach. Ensure all cryptographic operations use standard, modern, and secure primitives from `.NET`'s `System.Security.Cryptography` namespace. Avoid implementing cryptographic primitives from scratch.
*   **Dependencies**: The project targets a modern .NET version. Ensure cross-platform compatibility.
