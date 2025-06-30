# Percolator.CryptographyTests

This project contains the complete unit test suite for the `Percolator.Cryptography` library. The primary goal is to ensure the cryptographic implementation is correct, secure, and robust.

## Testing Philosophy

*   **Test-Driven Development (TDD)**: The library was built following TDD principles. Every feature in the main library has corresponding tests in this project.
*   **Security-First**: Tests cover not only the "happy path" but also critical security failure modes, such as attempts to use tampered messages or invalid signatures.
*   **Clarity**: Tests are written to be readable and to serve as documentation for the library's expected behavior. `FluentAssertions` is used for its expressive syntax.

## Key Test Scenarios

*   **`X3DHManagerTests`**:
    *   Verifies that a full, successful handshake results in both parties deriving the exact same shared secret.
    *   Ensures that a handshake attempt with an invalid `ECDSA` signature is rejected with a `CryptographicException`.
*   **`DoubleRatchetSessionTests`**:
    *   Confirms a full encrypt-decrypt cycle.
    *   Verifies that both the symmetric (chain key) and asymmetric (Diffie-Hellman) ratchets advance correctly.
    *   Ensures that decrypting a tampered message is rejected with an exception wrapping an `AuthenticationTagMismatchException`.
    *   Proves that the session can correctly handle messages arriving out of order by using its key cache.

## Guidance for AI Assistants

*   **Mandatory Testing**: Any new feature or bug fix in the `Percolator.Cryptography` library **must** be accompanied by a new or updated test in this project.
*   **Internal Access**: The test project uses `<InternalsVisibleTo>` in the `Percolator.Cryptography.csproj` file to access internal members of the main library (like `SendingChainKey`). This is intentional and necessary for verifying the internal state of the ratchet during tests.
*   **Test Structure**: Follow the existing Arrange-Act-Assert pattern. Create separate test methods for distinct behaviors.
*   **Cryptographic Helpers**: The test suite contains helper methods (e.g., `CreateKeyPair`) to reduce boilerplate when setting up cryptographic objects for tests. Use them.
*   **No Useless Tests**: Do not write tests that only verify that a mock or fake was called. Tests must validate real business logic and outcomes, not the setup of the test itself. A test that only asserts `mock.Verify(d => d.DoWork())` is useless. Instead, test the actual result of the system under test's operation, which may rely on the mock's output.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.
