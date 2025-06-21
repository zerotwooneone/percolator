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
