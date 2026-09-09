using System;
using System.Collections.Generic;
using System.Linq;
using VGStockpile.Transfers.Persistence;

namespace VGStockpile.Transfers.Engine;

internal enum TransferError { None, InsufficientCredits, QueueFull, EmptyManifest, SessionUnavailable, PersistenceUnavailable, DistanceUnavailable }

internal readonly record struct TransferRequestResult(
    bool IsSuccess, TransferError Error, TransferRequest? Created);

internal sealed class TransferEngine
{
    private readonly TransferQueue _queue;
    private readonly IMaterialStorageMutator _mutator;
    private readonly ICreditsMutator _credits;
    private readonly TransferConfig _cfg;
    internal Func<bool>? OperationAllowed { get; set; }
    internal bool ValidateEtaForPersistence { get; set; }
    internal Func<bool>? QueryAllowed { get; set; }
    internal Func<TransferError>? UnavailableReason { get; set; }
    private bool CanOperate => OperationAllowed?.Invoke() != false;
    private readonly Func<string> _idGen;

    public TransferEngine(
        TransferQueue queue,
        IMaterialStorageMutator mutator,
        ICreditsMutator credits,
        TransferConfig cfg,
        Func<string>? idGen = null)
    {
        _queue = queue; _mutator = mutator; _credits = credits;
        _cfg = cfg;
        _idGen = idGen ?? (() => Guid.NewGuid().ToString("N"));
    }

    public IReadOnlyList<TransferRequest> Pending => (QueryAllowed?.Invoke() ?? CanOperate) ? _queue.Items : Array.Empty<TransferRequest>();

    public TransferRequestResult RequestTransfer(
        string sourceGuid, string destGuid,
        IReadOnlyList<TransferManifestLine> manifest, int jumpDistance)
    {
        if (!CanOperate) return new TransferRequestResult(false, UnavailableReason?.Invoke() ?? TransferError.SessionUnavailable, null);
        if (jumpDistance < 0) return new TransferRequestResult(false, TransferError.DistanceUnavailable, null);
        manifest = manifest.ToArray();
        if (manifest.Count == 0)
            return new TransferRequestResult(false, TransferError.EmptyManifest, null);
        if (!_queue.CanAccept)
            return new TransferRequestResult(false, TransferError.QueueFull, null);

        var totalUnits = 0; for (var i = 0; i < manifest.Count; i++) totalUnits += manifest[i].Quantity;
        var fee  = FeeCalculator.Compute(jumpDistance, totalUnits, _cfg);
        var eta  = EtaCalculator.ComputeSeconds(jumpDistance, _cfg);
        if (ValidateEtaForPersistence && (float.IsNaN(eta) || float.IsInfinity(eta) || eta < 0))
            return new TransferRequestResult(false, TransferError.PersistenceUnavailable, null);

        if (!_credits.TryDebit(fee))
            return new TransferRequestResult(false, TransferError.InsufficientCredits, null);

        _mutator.Reserve(sourceGuid, manifest);

        var req = new TransferRequest(
            Id: _idGen(), SourceStationGuid: sourceGuid, DestStationGuid: destGuid,
            Manifest: manifest, FeeCredits: fee, JumpDistance: jumpDistance,
            TotalSeconds: eta, RemainingSeconds: eta, Status: TransferStatus.Pending);

        _queue.Add(req);
        return new TransferRequestResult(true, TransferError.None, req);
    }

    public bool CancelTransfer(string id)
    {
        if (!CanOperate) return false;
        var req = _queue.Items.FirstOrDefault(r => r.Id == id);
        if (req is null) return false;
        if (!_queue.Cancel(id)) return false;
        _mutator.Return(req.SourceStationGuid, req.Manifest);
        return true;
    }

    public IReadOnlyList<TransferRequest> Tick(float dt)
    {
        if (!CanOperate) return Array.Empty<TransferRequest>();
        var completed = _queue.TickSeconds(dt);
        if (completed.Count == 0) return Array.Empty<TransferRequest>();

        for (var i = 0; i < completed.Count; i++)
            _mutator.Deliver(completed[i].DestStationGuid, completed[i].Manifest);

        _queue.RemoveTerminal();
        return completed;
    }

    internal void Restore(TransferSidecar sidecar) => _queue.LoadFrom(sidecar.Items);

    // Persistence bypasses the UI/mutation gate to capture a frozen in-flight save.
    internal TransferSidecar Snapshot() => new(TransferSidecar.CurrentVersion,
        _queue.Items.Select(r => r with { Manifest = r.Manifest.ToArray() }).ToArray());
}
