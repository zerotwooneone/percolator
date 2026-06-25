namespace Percolator.NetworkTests;

// Contract tests specifying expected behavior of IDirectSessionRepository
[TestFixture]
[Ignore("Contract-only spec; behavior validated in Infrastructure tests")] 
public class DirectSessionRepositoryContractTests
{
    private Percolator.Network.IDirectSessionRepository _repo = default!; // To be provided by concrete impl in infra tests

    [Test]
    public async Task GetBySessionIdAsync_returns_null_for_unknown_session()
    {
        var unknown = Guid.NewGuid();
        var result = await _repo.GetBySessionIdAsync(new Percolator.Network.DirectSessionId(unknown), 1);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Upsert_then_GetBySessionIdAsync_returns_entry()
    {
        var peerId = new Percolator.Network.PeerId(1);
        var sessionId = Guid.NewGuid();
        await _repo.UpsertAsync(peerId, new Percolator.Network.DirectSessionId(sessionId), 1);
        var ds = await _repo.GetBySessionIdAsync(new Percolator.Network.DirectSessionId(sessionId), 1);
        Assert.That(ds, Is.Not.Null);
        Assert.That(ds!.RemotePeerId.Value, Is.EqualTo(peerId.Value));
        Assert.That(ds.SessionId.Value, Is.EqualTo(sessionId));
    }

    [Test]
    public async Task Upsert_overwrites_existing_session_for_same_peer()
    {
        var peerId = new Percolator.Network.PeerId(1);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await _repo.UpsertAsync(peerId, new Percolator.Network.DirectSessionId(s1), 1);
        await _repo.UpsertAsync(peerId, new Percolator.Network.DirectSessionId(s2), 1);
        var ds = await _repo.GetBySessionIdAsync(new Percolator.Network.DirectSessionId(s2), 1);
        Assert.That(ds, Is.Not.Null);
        Assert.That(ds!.RemotePeerId.Value, Is.EqualTo(peerId.Value));
        Assert.That(ds.SessionId.Value, Is.EqualTo(s2));
        var old = await _repo.GetBySessionIdAsync(new Percolator.Network.DirectSessionId(s1), 1);
        Assert.That(old, Is.Null);
    }
}
