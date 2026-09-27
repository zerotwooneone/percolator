using Percolator.Domain.Common;

namespace Percolator.Domain.Tests.Common;

[TestFixture]
public class DomainResultTests
{
    [Test]
    public void Success_ReturnsIsSuccessTrue_AndNoError()
    {
        var result = DomainResult.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().Be(DomainError.None);
    }

    [Test]
    public void Failure_CarriesErrorCodeAndDescription_AndIsFailureTrue()
    {
        var error = new DomainError("TEST_CODE", "Test description");
        var result = DomainResult.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("TEST_CODE");
        result.Error.Description.Should().Be("Test description");
    }

    [Test]
    public void SuccessOfT_CarriesValue_AndIsSuccessTrue()
    {
        var result = DomainResult<string>.Success("test-payload");

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be("test-payload");
        result.Error.Should().Be(DomainError.None);
    }

    [Test]
    public void FailureOfT_CarriesError_AndIsFailureTrue()
    {
        var error = new DomainError("NOT_FOUND", "Item was not found");
        var result = DomainResult<string>.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NOT_FOUND");
    }

    [Test]
    public void FailureOfT_AccessingValue_ThrowsInvalidOperationException()
    {
        var error = new DomainError("NOT_FOUND", "Item was not found");
        var result = DomainResult<string>.Failure(error);

        var act = () => _ = result.Value;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOT_FOUND*Item was not found*");
    }

    [Test]
    public void ImplicitConversion_FromValue_CreatesSuccessResult()
    {
        DomainResult<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().Be(DomainError.None);
    }
}
