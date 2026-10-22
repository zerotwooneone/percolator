using Percolator.Application2IntegrationTests.Infrastructure;

namespace Percolator.Application2IntegrationTests.Builders;

public sealed class TestCluster
{
    private readonly Dictionary<string, TestIdentityNode> _nodesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly TestDateTimeProvider _timeProvider;

    public InMemoryLoopbackNetworkSwitch Switch { get; }
    public IReadOnlyCollection<TestIdentityNode> Nodes => _nodesByName.Values;

    public TestCluster(
        InMemoryLoopbackNetworkSwitch networkSwitch,
        IEnumerable<TestIdentityNode> nodes,
        TestDateTimeProvider timeProvider)
    {
        Switch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        foreach (var node in nodes)
        {
            _nodesByName[node.Name] = node;
        }
    }

    public void AdvanceTime(TimeSpan delta) => _timeProvider.Advance(delta);

    public TestIdentityNode GetNode(string name)
    {
        if (!_nodesByName.TryGetValue(name, out var node))
        {
            throw new KeyNotFoundException($"Node with name '{name}' not found in cluster.");
        }

        return node;
    }

    public TestIdentityNode this[string name] => GetNode(name);
}

public sealed class TestClusterBuilder
{
    private readonly List<(string Name, EndpointPort Port)> _nodeDefs = [];
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch = new();
    private readonly ScenarioCryptoEngine _sharedCryptoEngine = new();
    private readonly TestDateTimeProvider _sharedTimeProvider = new();

    public TestClusterBuilder AddNode(string name, EndpointPort port)
    {
        _nodeDefs.Add((name, port));
        return this;
    }

    public TestClusterBuilder AddNode(string name, int port)
    {
        return AddNode(name, new EndpointPort(port));
    }

    public TestCluster Build()
    {
        var nodes = new List<TestIdentityNode>();

        foreach (var (name, port) in _nodeDefs)
        {
            var node = new TestIdentityNode(
                name,
                port,
                _networkSwitch,
                _sharedCryptoEngine,
                _sharedTimeProvider);

            nodes.Add(node);
        }

        return new TestCluster(_networkSwitch, nodes, _sharedTimeProvider);
    }
}
