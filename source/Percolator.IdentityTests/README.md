# Percolator.IdentityTests

This project contains the complete unit test suite for the `Percolator.Identity` library. The goal is to ensure that identity management, including credential storage and certificate handling, is correct and secure.

## Testing Philosophy

*   **Isolation**: Tests are designed to run in isolation, with no side effects on the user's actual file system. Test-specific paths are used and cleaned up after each run.
*   **Dependencies**: Dependencies like `ICredentialService` are mocked using `Moq` to ensure that tests for a given service (e.g., `PersistentIdentityService`) are focused only on that service's logic.
*   **Clarity**: Tests are written to be readable and to serve as documentation for the library's expected behavior. `FluentAssertions` is used for its expressive syntax.

## Key Test Scenarios

*   **`CredentialServiceTests`**:
    *   Verifies that a password file is created if one does not exist.
    *   Ensures that an existing password file is read correctly and the same password is returned on subsequent calls.
*   **`PersistentIdentityServiceTests`**:
    *   Confirms that a new identity certificate is created when one is not found.
    *   Verifies that an existing identity certificate is loaded correctly.
    *   Ensures that a `CryptographicException` is thrown when attempting to load a corrupt certificate file.

## Guidance for AI Assistants

*   **Mandatory Testing**: Any new feature or bug fix in the `Percolator.Identity` library **must** be accompanied by a new or updated test in this project.
*   **Internal Access**: The test project uses `<InternalsVisibleTo>` to access `internal` constructors on services. This is by design to allow the injection of test-specific dependencies, such as file paths, without exposing them in the public API.
*   **No Useless Tests**: Do not write tests that only verify that a mock or fake was called. Tests must validate real business logic and outcomes, not the setup of the test itself. A test that only asserts `mock.Verify(d => d.DoWork())` is useless. Instead, test the actual result of the system under test's operation, which may rely on the mock's output.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.
