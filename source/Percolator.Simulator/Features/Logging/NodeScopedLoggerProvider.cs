using System;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Logging;

internal sealed class NodeScopedLoggerProvider : ILoggerProvider
{
    private readonly string _nodeName;
    private readonly SimulatedNodeId _nodeId;
    private readonly ISimulatorLogSink _logSink;

    public NodeScopedLoggerProvider(string nodeName, SimulatedNodeId nodeId, ISimulatorLogSink logSink)
    {
        _nodeName = nodeName;
        _nodeId = nodeId;
        _logSink = logSink;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new NodeScopedLogger(_nodeName, _nodeId, categoryName, _logSink);
    }

    public void Dispose()
    {
    }

    private sealed class NodeScopedLogger : ILogger
    {
        private readonly string _nodeName;
        private readonly SimulatedNodeId _nodeId;
        private readonly string _categoryName;
        private readonly ISimulatorLogSink _logSink;

        public NodeScopedLogger(string nodeName, SimulatedNodeId nodeId, string categoryName, ISimulatorLogSink logSink)
        {
            _nodeName = nodeName;
            _nodeId = nodeId;
            _categoryName = categoryName;
            _logSink = logSink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var entry = new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                logLevel,
                _nodeName,
                _nodeId,
                _categoryName,
                message,
                exception?.ToString());

            _logSink.Log(entry);
        }
    }
}
