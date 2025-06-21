# Percolator.NetworkTests

This project contains the unit test suite for the `Percolator.Network` library. The tests focus on ensuring the reliability and correctness of peer-to-peer networking logic.

## Testing Philosophy

*   **Focused Tests**: Each test class is focused on a specific component of the networking stack (e.g., `PeerConnectionManager`, `PeerDiscoveryService`).
*   **State Management**: Tests verify that the state of network components (e.g., the list of known peers) is managed correctly in response to network events.
*   **Clarity**: Tests are written to be readable and to serve as documentation for the library's expected behavior. `FluentAssertions` is used for its expressive syntax.

## Guidance for AI Assistants

*   **Mandatory Testing**: Any new feature or bug fix in the `Percolator.Network` library **must** be accompanied by a new or updated test in this project.
*   **No Useless Tests**: Do not write tests that only verify that a mock or fake was called. Tests must validate real business logic and outcomes, not the setup of the test itself. A test that only asserts `mock.Verify(d => d.DoWork())` is useless. Instead, test the actual result of the system under test's operation, which may rely on the mock's output.
