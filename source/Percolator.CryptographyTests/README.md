# Percolator.CryptographyTests

This project contains the unit tests for the `Percolator.Cryptography` domain library. The primary goal is to ensure the correctness, security, and robustness of the cryptographic logic in complete isolation from the rest of the application.

## Testing Philosophy

Our testing strategy for the cryptography domain is guided by the following principles:

1.  **Domain Purity**: Tests must verify the domain logic in isolation. There should be absolutely no dependencies on external infrastructure like networks, file systems, or databases.
2.  **Deterministic Tests**: All tests must be 100% deterministic. Any sources of randomness (e.g., for key generation or nonces) must be mocked or controlled to ensure that tests produce the same output every single time.
3.  **Clarity and Readability**: Tests should be highly readable and serve as living documentation for the cryptographic protocols. We use a combination of clear naming conventions and expressive assertions to achieve this.
4.  **Correctness over Performance**: While performance is important, the primary focus of these tests is to rigorously verify the correctness of the cryptographic implementations.

## Testing Frameworks & Libraries

We use a combination of industry-standard libraries to write effective and maintainable tests:

-   **NUnit**: The core test framework used to define, structure, and run our tests.
-   **Moq**: A powerful and flexible mocking library used to create test doubles and isolate the code under test from its dependencies.
-   **FluentAssertions**: Provides a set of extension methods that allow for writing more readable and natural-language-like assertions.
-   **AutoFixture**: Used to automate the generation of test data and objects, reducing boilerplate setup code and making tests easier to write and maintain.
