using System;

namespace Percolator.Simulator.Features.Scenarios.Model;

public class ScenarioExecutionException : Exception
{
    public ScenarioExecutionException(string message) : base(message) { }
    public ScenarioExecutionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ScenarioAssertionException : ScenarioExecutionException
{
    public ScenarioAssertionException(string message) : base(message) { }
}
