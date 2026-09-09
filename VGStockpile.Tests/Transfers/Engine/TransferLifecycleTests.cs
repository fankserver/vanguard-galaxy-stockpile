using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
using VGStockpile.Transfers;
using VGStockpile.Transfers.Engine;
using VGStockpile.Transfers.Persistence;
using Xunit;

namespace VGStockpile.Tests.Transfers.Engine;

public sealed class TransferLifecycleTests : IDisposable
{
    private readonly FakeApi _api = new();
    private readonly Materials _materials = new();
    private readonly Credits _credits = new();
    private readonly Store _store = new();
    private readonly TransferEngine _engine;
    private readonly TransferLifecycle _lifecycle;
    private SessionSnapshot _session = null!;
    public TransferLifecycleTests()
    {
        _engine = new TransferEngine(new TransferQueue(0), _materials, _credits, TransferConfig.Defaults());
        _lifecycle = new TransferLifecycle(_api, _engine, _store, _ => { }, () => { }, _ => { });
    }
    private void Ready(bool gameplay = true, string path = "source.save")
    {
        var id = Guid.NewGuid();
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SessionStarting, new SessionSnapshot(id, SessionPhase.Starting, SessionOrigin.SaveLoad, path)));
        _session = new SessionSnapshot(id, SessionPhase.PlayerReady, SessionOrigin.SaveLoad, path);
        _api.Emit(new LifecycleEvent(LifecycleEventKind.PlayerReady, _session));
        if (!gameplay) return;
        _session = new SessionSnapshot(id, SessionPhase.GameplayInitialized, SessionOrigin.SaveLoad, path);
        _api.Emit(new LifecycleEvent(LifecycleEventKind.GameplayInitialized, _session));
    }
    private TransferRequest Request()
    {
        var result = _engine.RequestTransfer("src", "dst", new[] { new TransferManifestLine("iron", 10) }, 0);
        Assert.True(result.IsSuccess); return result.Created!;
    }
    private Guid Start(string path = "target.save")
    {
        var id = Guid.NewGuid();
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SaveStarted, _session, id, path), false); return id;
    }
    private void Finish(Guid id, LifecycleEventKind kind = LifecycleEventKind.SaveSucceeded, string path = "target.save")
        => _api.Emit(new LifecycleEvent(kind, _session, id, path), false);

    [Fact]
    public void CallerCannotAlterReservedManifest()
    {
        Ready();
        var manifest = new List<TransferManifestLine> { new("iron", 10) };
        Assert.True(_engine.RequestTransfer("src", "dst", manifest, 0).IsSuccess);
        manifest.Clear(); manifest.Add(new TransferManifestLine("iron", 99));
        Finish(Start());
        Assert.Equal(10, Assert.Single(Assert.Single(_store.Writes).state.Items).Manifest.Single().Quantity);
        Assert.Equal(90, _materials.Source);
    }

    [Fact]
    public void LoadingAndPlayerReadyCannotMutate()
    {
        Ready(false);
        var result = _engine.RequestTransfer("src", "dst", new[] { new TransferManifestLine("iron", 10) }, 0);
        Assert.Equal(TransferError.SessionUnavailable, result.Error);
        Assert.Equal(100, _materials.Source); Assert.Equal(10000, _credits.Value);
    }
    [Fact]
    public void MutationsStayInMemoryUntilMatchingSuccessfulSave()
    {
        Ready(); var request = Request();
        Assert.Empty(_store.Writes); Assert.Equal(90, _materials.Source);
        Assert.Equal(10000 - request.FeeCredits, _credits.Value);
        var id = Start("save-as.save");
        Assert.Empty(_engine.Tick(float.MaxValue)); Assert.False(_engine.CancelTransfer(request.Id));
        Assert.Equal(TransferError.SessionUnavailable, _engine.RequestTransfer("src", "dst", request.Manifest, 0).Error);
        Finish(id, path: "save-as.save");
        var written = Assert.Single(_store.Writes);
        Assert.Equal(SavePathResolver.Sidecar("save-as.save"), written.path);
        Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(request), Newtonsoft.Json.JsonConvert.SerializeObject(Assert.Single(written.state.Items)));
        _engine.Tick(float.MaxValue);
        Assert.Equal(10, _materials.Destination); Assert.Single(_store.Writes);
    }
    [Theory]
    [InlineData(LifecycleEventKind.SaveFailed)]
    [InlineData(LifecycleEventKind.SaveSkipped)]
    public void NonSuccessDoesNotPersistOrLoseQueue(LifecycleEventKind kind)
    {
        Ready(); Request(); var id = Start(); Finish(id, kind);
        Assert.Empty(_store.Writes); Assert.Single(_engine.Pending);
    }
    [Fact]
    public void ReplacementDropsOldQueueWithoutReturningOldInventory()
    {
        Ready(); Request(); var old = _session; var id = Start(); Ready();
        _api.Emit(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, old, id, "stale.save"), false);
        Assert.Empty(_engine.Pending); Assert.Empty(_store.Writes); Assert.Equal(90, _materials.Source);
    }
    [Fact]
    public void ReentrantCurrentReplacementRejectsQueuedOldSuccess()
    {
        Ready(); Request(); var id = Start();
        _api.CurrentSession = new SessionSnapshot(Guid.NewGuid(), SessionPhase.Starting, SessionOrigin.NewGame, null);
        Finish(id); Assert.Empty(_store.Writes); Assert.Empty(_engine.Pending);
    }
    [Fact]
    public void DispatchItselfBlocksMutationsWithoutAnObservedSave()
    {
        Ready(); Request(); _api.IsDispatchingCallbacks = true;
        Assert.Empty(_engine.Tick(float.MaxValue)); Assert.Equal(0, _materials.Destination);
        _api.IsDispatchingCallbacks = false;
        Assert.Single(_engine.Tick(float.MaxValue));
    }
    [Fact]
    public void NestedSaveCompletionDoesNotUnfreezeOuterSave()
    {
        Ready(); Request(); var outer = Start(); var inner = Start("inner.save");
        Finish(inner, path: "inner.save"); Assert.Empty(_engine.Tick(float.MaxValue));
        Finish(outer); Assert.Equal(2, _store.Writes.Count); Assert.Single(_engine.Tick(float.MaxValue));
    }
    [Fact]
    public void WriteFaultPausesMutationsUntilSuccessfulRetry()
    {
        Ready(); Request(); _store.Fail = true; Finish(Start());
        Assert.Empty(_engine.Tick(float.MaxValue));
        _store.Fail = false; Finish(Start());
        Assert.Single(_engine.Tick(float.MaxValue));
    }
    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"Version\":99,\"Items\":[]}")]
    public void RealStoreRestoreRefusalDisablesAttemptAndPreservesFile(string json)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "source.save");
            var sidecar = SavePathResolver.Sidecar(path);
            System.IO.File.WriteAllText(sidecar, json);
            _lifecycle.Dispose();
            using var lifecycle = new TransferLifecycle(_api, _engine, new JsonTransferStore(_ => { }), _ => { }, () => { }, _ => { });
            Ready(path: path);
            Assert.Equal(TransferError.PersistenceUnavailable, _engine.RequestTransfer("src", "dst", new[] { new TransferManifestLine("iron", 10) }, 0).Error);
            Finish(Start(path), path: path);
            Assert.Equal(100, _materials.Source); Assert.Equal(10000, _credits.Value);
            Assert.Equal(json, System.IO.File.ReadAllText(sidecar));
            Assert.Single(System.IO.Directory.GetFiles(root));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"Version\":99,\"Items\":[]}")]
    public void RealStoreWriteRefusalPausesVisibleQueueUntilCleanSaveAs(string json)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "source.save");
            _lifecycle.Dispose();
            using var lifecycle = new TransferLifecycle(_api, _engine, new JsonTransferStore(_ => { }), _ => { }, () => { }, _ => { });
            Ready(path: path); Request();
            var sidecar = SavePathResolver.Sidecar(path);
            System.IO.File.WriteAllText(sidecar, json);
            Finish(Start(path), path: path);
            Assert.Single(_engine.Pending);
            Assert.Empty(_engine.Tick(float.MaxValue));
            Assert.Equal(TransferError.PersistenceUnavailable, _engine.RequestTransfer("src", "dst", new[] { new TransferManifestLine("iron", 10) }, 0).Error);
            Assert.Equal(json, System.IO.File.ReadAllText(sidecar));
            var retry = System.IO.Path.Combine(root, "retry.save");
            Finish(Start(retry), path: retry);
            Assert.True(System.IO.File.Exists(SavePathResolver.Sidecar(retry)));
            Assert.Single(_engine.Tick(float.MaxValue));
            Assert.Equal(json, System.IO.File.ReadAllText(sidecar));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Fact]
    public void DisposalBlocksStaleCallbacksAndDriver()
    {
        Ready(); Request(); var id = Start(); _lifecycle.Dispose(); _lifecycle.Dispose(); Finish(id);
        Assert.Empty(_store.Writes); Assert.Empty(_engine.Tick(float.MaxValue)); Assert.Equal(90, _materials.Source);
    }
    [Fact]
    public void RestoreResumesQueueWithoutSecondDebitOrReservation()
    {
        Ready(); var request = Request(); Finish(Start());
        _store.Loaded = _store.Writes.Single().state;
        var credits = _credits.Value; Ready();
        Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(request), Newtonsoft.Json.JsonConvert.SerializeObject(Assert.Single(_engine.Pending)));
        Assert.Equal(credits, _credits.Value); Assert.Equal(90, _materials.Source);
    }
    public void Dispose() => _lifecycle.Dispose();
    private sealed class Materials : IMaterialStorageMutator
    {
        internal int Source = 100, Destination;
        public void Reserve(string s, IReadOnlyList<TransferManifestLine> m) => Source -= m.Sum(x => x.Quantity);
        public void Return(string s, IReadOnlyList<TransferManifestLine> m) => Source += m.Sum(x => x.Quantity);
        public void Deliver(string s, IReadOnlyList<TransferManifestLine> m) => Destination += m.Sum(x => x.Quantity);
    }
    private sealed class Credits : ICreditsMutator
    {
        internal int Value = 10000;
        public int Current => Value;
        public bool TryDebit(int amount) { if (Value < amount) return false; Value -= amount; return true; }
    }
    private sealed class Store : ITransferStore
    {
        internal bool Fail;
        internal TransferSidecar Loaded = TransferSidecar.Empty();
        internal readonly List<(string path, TransferSidecar state)> Writes = new();
        public TransferSidecar Load(string path) => Loaded;
        public void Save(string path, TransferSidecar state) { if (Fail) throw new InvalidOperationException("test"); Writes.Add((path, state)); }
    }
    private sealed class FakeApi : ILifecycleService
    {
        public event Action<LifecycleEvent>? Changed;
        public bool IsDispatchingCallbacks { get; set; }
        public SessionSnapshot? CurrentSession { get; set; }
        public IServiceStatus SessionTracking { get; } = new TestServiceStatus();
        public IServiceStatus SaveOutcomes { get; } = new TestServiceStatus();
        internal void Emit(LifecycleEvent e, bool update = true)
        {
            if (update) CurrentSession = e.Session;
            IsDispatchingCallbacks = true;
            try { Changed?.Invoke(e); } finally { IsDispatchingCallbacks = false; }
        }
    }
    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        internal Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() { var d = _dispose; _dispose = null; d?.Invoke(); }
    }
}
