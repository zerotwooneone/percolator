# Percolator.NetworkTests

This project contains the unit test suite for the `Percolator.Network` library. The tests focus on ensuring the reliability and correctness of peer-to-peer networking logic.

## Testing Philosophy

*   **Focused Tests**: Each test class is focused on a specific component of the networking stack (e.g., `PeerConnectionManager`, `PeerDiscoveryService`).
*   **State Management**: Tests verify that the state of network components (e.g., the list of known peers) is managed correctly in response to network events.
*   **Clarity**: Tests are written to be readable and to serve as documentation for the library's expected behavior. `FluentAssertions` is used for its expressive syntax.

## Guidance for AI Assistants

*   **Mandatory Testing**: Any new feature or bug fix in the `Percolator.Network` library **must** be accompanied by a new or updated test in this project.
*   **No Useless Tests**: Do not write tests that only verify that a mock or fake was called. Tests must validate real business logic and outcomes, not the setup of the test itself. A test that only asserts `mock.Verify(d => d.DoWork())` is useless. Instead, test the actual result of the system under test's operation, which may rely on the mock's output.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.
