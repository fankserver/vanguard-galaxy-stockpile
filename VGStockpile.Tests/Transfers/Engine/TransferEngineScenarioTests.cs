using System.Collections.Generic;
using System.Linq;
using VGStockpile.Transfers;
using VGStockpile.Transfers.Engine;
using VGStockpile.Transfers.Persistence;
using Xunit;

namespace VGStockpile.Tests.Transfers.Engine;

public class TransferEngineScenarioTests
{
    private sealed class FakeMutator : IMaterialStorageMutator
    {
        public List<(string action, string station, IReadOnlyList<TransferManifestLine> m)> Calls = new();
        public void Reserve(string s, IReadOnlyList<TransferManifestLine> m) => Calls.Add(("Reserve", s, m));
        public void Deliver(string s, IReadOnlyList<TransferManifestLine> m) => Calls.Add(("Deliver", s, m));
        public void Return (string s, IReadOnlyList<TransferManifestLine> m) => Calls.Add(("Return",  s, m));
    }

    private sealed class FakeCredits : ICreditsMutator
    {
        public int Current { get; set; } = 10_000;
        public List<int> Debits = new();
        public bool TryDebit(int amount)
        {
            if (Current < amount) return false;
            Current -= amount; Debits.Add(amount); return true;
        }
    }

    private static readonly TransferConfig Cfg = TransferConfig.Defaults() with
    {
        Enabled = true,
        EtaBaseSeconds = 30f, EtaPerJumpSeconds = 0f,
        EtaMinSeconds = 30f, EtaMaxSeconds = 30f,
    };

    [Fact]
    public void UnknownDistanceRefusesBeforeReservationDebitOrQueueMutation()
    {
        var mutator = new FakeMutator(); var credits = new FakeCredits();
        var queue = new TransferQueue(5);
        var engine = new TransferEngine(queue, mutator, credits, Cfg);
        var result = engine.RequestTransfer("src", "dst", new[] { new TransferManifestLine("iron", 10) }, -1);
        Assert.False(result.IsSuccess); Assert.Equal(TransferError.DistanceUnavailable, result.Error);
        Assert.Empty(mutator.Calls); Assert.Empty(credits.Debits); Assert.Empty(engine.Pending);
    }
    [Fact]
    public void Scenario_QueueTickDeliver_FiresMutationsInOrder()
    {
        var mutator = new FakeMutator();
        var credits = new FakeCredits();
        var manifest = new List<TransferManifestLine> { new("iron", 100) };

        var engine = new TransferEngine(
            queue: new TransferQueue(maxConcurrent: 0),
            mutator: mutator, credits: credits,
            cfg: Cfg,
            idGen: () => "fixed-id");

        var result = engine.RequestTransfer("src", "dst", manifest, jumpDistance: 0);
        Assert.True(result.IsSuccess);

        Assert.Single(mutator.Calls, c => c.action == "Reserve" && c.station == "src");
        Assert.Single(credits.Debits);
        Assert.Equal(200, credits.Debits[0]);
        Assert.Single(engine.Snapshot().Items);

        engine.Tick(10f);
        Assert.DoesNotContain(mutator.Calls, c => c.action == "Deliver");

        engine.Tick(25f);
        Assert.Contains(mutator.Calls, c => c.action == "Deliver" && c.station == "dst");
        Assert.Empty(engine.Snapshot().Items);
    }

    [Fact]
    public void Scenario_Cancel_TriggersReturnAndKeepsCreditsDebited()
    {
        var mutator = new FakeMutator(); var credits = new FakeCredits();
        var manifest = new List<TransferManifestLine> { new("iron", 100) };

        var engine = new TransferEngine(
            new TransferQueue(0), mutator, credits,
            Cfg, () => "id-1");

        engine.RequestTransfer("src", "dst", manifest, 0);
        Assert.True(engine.CancelTransfer("id-1"));

        Assert.Contains(mutator.Calls, c => c.action == "Return" && c.station == "src");
        Assert.Equal(10_000 - 200, credits.Current);
    }

    [Fact]
    public void Scenario_TickPastZero_DoesNotDoubleDeliver()
    {
        var mutator = new FakeMutator(); var credits = new FakeCredits();
        var engine = new TransferEngine(
            new TransferQueue(0), mutator, credits,
            Cfg, () => "id-1");
        engine.RequestTransfer("src", "dst",
            new List<TransferManifestLine> { new("iron", 1) }, 0);

        engine.Tick(60f);
        engine.Tick(60f);

        Assert.Single(mutator.Calls, c => c.action == "Deliver");
    }

    [Fact]
    public void Scenario_InsufficientCredits_RejectsBeforeMutation()
    {
        var mutator = new FakeMutator();
        var credits = new FakeCredits { Current = 50 };
        var engine = new TransferEngine(
            new TransferQueue(0), mutator, credits,
            Cfg, () => "id-1");

        var result = engine.RequestTransfer("src", "dst",
            new List<TransferManifestLine> { new("iron", 1) }, 0);

        Assert.False(result.IsSuccess);
        Assert.Equal(TransferError.InsufficientCredits, result.Error);
        Assert.Empty(mutator.Calls);
    }

    [Fact]
    public void Scenario_QueueFull_RejectsBeforeAnyMutation()
    {
        var mutator = new FakeMutator(); var credits = new FakeCredits();
        var queue = new TransferQueue(maxConcurrent: 1);
        var engine = new TransferEngine(
            queue, mutator, credits,
            Cfg, () => System.Guid.NewGuid().ToString("N"));

        // Fill the queue to capacity.
        var first = engine.RequestTransfer("src", "dst",
            new List<TransferManifestLine> { new("iron", 1) }, 0);
        Assert.True(first.IsSuccess);
        var creditsBefore = credits.Current;
        var callsBefore = mutator.Calls.Count;

        // The next request should be rejected with QueueFull.
        var second = engine.RequestTransfer("src", "dst",
            new List<TransferManifestLine> { new("iron", 1) }, 0);

        Assert.False(second.IsSuccess);
        Assert.Equal(TransferError.QueueFull, second.Error);
        Assert.Equal(creditsBefore, credits.Current);    // no debit
        Assert.Equal(callsBefore, mutator.Calls.Count);  // no Reserve
    }
}
