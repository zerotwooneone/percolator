using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Delivery.Hosting;

namespace Percolator.Application2IntegrationTests.Builders;

public sealed class TestCluster
{
    private readonly Dictionary<string, TestIdentityNode> _nodesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelayNode> _relaysByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly TestDateTimeProvider _timeProvider;

    public InMemoryLoopbackNetworkSwitch Switch { get; }
    public IReadOnlyCollection<TestIdentityNode> Nodes => _nodesByName.Values;
    public IReadOnlyCollection<RelayNode> Relays => _relaysByName.Values;

    public TestCluster(
        InMemoryLoopbackNetworkSwitch networkSwitch,
        IEnumerable<TestIdentityNode> nodes,
        IEnumerable<RelayNode> relays,
        TestDateTimeProvider timeProvider)
    {
        Switch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        foreach (var node in nodes)
        {
            _nodesByName[node.Name] = node;
        }

        foreach (var relay in relays)
        {
            _relaysByName[relay.Name] = relay;
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

    public RelayNode GetRelay(string name)
    {
        if (!_relaysByName.TryGetValue(name, out var relay))
        {
            throw new KeyNotFoundException($"Relay with name '{name}' not found in cluster.");
        }

        return relay;
    }

    public TestIdentityNode this[string name] => GetNode(name);
}

public sealed class TestClusterBuilder
{
    private readonly List<(string Name, EndpointPort Port)> _nodeDefs = [];
    private readonly List<(string Name, EndpointPort Port, RelayHostingPolicy? Policy)> _relayDefs = [];
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

    public TestClusterBuilder AddRelayNode(string name, EndpointPort port, RelayHostingPolicy? policy = null)
    {
        _relayDefs.Add((name, port, policy));
        return this;
    }

    public TestClusterBuilder AddRelayNode(string name, int port, RelayHostingPolicy? policy = null)
    {
        return AddRelayNode(name, new EndpointPort(port), policy);
    }

    public TestCluster Build()
    {
        var relays = new List<RelayNode>();
        foreach (var (name, port, policy) in _relayDefs)
        {
            var relay = new RelayNode(name, port, policy, _sharedTimeProvider);
            _networkSwitch.RegisterRelay(relay);
            relays.Add(relay);
        }

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

        return new TestCluster(_networkSwitch, nodes, relays, _sharedTimeProvider);
    }
}
