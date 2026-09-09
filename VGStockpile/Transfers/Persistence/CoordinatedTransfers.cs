using System;
using VGModAPI;
using VGStockpile.Transfers.Engine;

namespace VGStockpile.Transfers.Persistence;

internal sealed class CoordinatedTransfers : ITransferPersistence
{
    private readonly ILifecycleService _lifecycle;
    private readonly TransferEngine? _engine;
    private readonly Action _resetUi;
    private readonly ISaveDataRegistration _registration;

    private TransferSidecar _retained = TransferSidecar.Empty();
    private Guid? _session;
    private bool _disposed;

    internal CoordinatedTransfers(ILifecycleService lifecycle, ISaveDataService api, TransferEngine? engine,
        bool importLegacy, Action<int> disabledPending, Action resetUi, Action<string> warn)
    {
        _lifecycle = lifecycle; _engine = engine; _resetUi = resetUi;
        if (engine != null) { engine.OperationAllowed = () => false; engine.QueryAllowed = () => false; }
        Clear();
        var registration = api.Register(new PersistenceProvider("vgstockpile", 1,
            () => TransferPayloadCodec.Encode(_engine?.Snapshot() ?? _retained),
            (session, payload) =>
            {
                try
                {
                    Clear(); _session = session.Id;
                    bool imported = false;
                    if (payload == null && session.SavePath != null)
                    {
                        var path = SavePathResolver.Sidecar(session.SavePath);
                        if (importLegacy) payload = TransferPayloadCodec.ReadLegacy(path);
                        else
                        {
                            try
                            {
                                using var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                                throw new System.IO.InvalidDataException("Existing transfer save data found. Enable ImportLegacySidecars to read it, or disable UseApiSaveData to keep legacy saves.");
                            }
                            catch (System.IO.FileNotFoundException) { }
                            catch (System.IO.DirectoryNotFoundException) { }
                        }
                        imported = payload != null;
                    }
                    var state = payload == null ? TransferSidecar.Empty() : TransferPayloadCodec.Decode(payload);
                    _retained = state; _engine?.Restore(state);
                    if (engine == null && state.Items.Count > 0) disabledPending(state.Items.Count);
                    if (imported) warn("Explicit read-only legacy transfer adoption; no historical snapshot consistency inferred.");
                }
                catch (Exception error)
                {
                    warn("Transfer save-data restore failed: " + error.GetType().Name + ": " + error.Message);
                    throw;
                }
            }, TransferPayloadCodec.IsValid));
        if (!registration.Succeeded) throw new InvalidOperationException("Transfer save registration refused: " + registration.Status + ": " + registration.Detail);
        _registration = registration.Registration!;
        try { lifecycle.Changed += Observe; }
        catch { _registration.Dispose(); throw; }
        if (engine != null)
        {
            engine.ValidateEtaForPersistence = true;
            engine.OperationAllowed = () => CanOperate;
            engine.QueryAllowed = () => CanOperate;
            engine.UnavailableReason = () => _registration.State.Kind is SaveDataStateKind.Inactive or SaveDataStateKind.Ready or SaveDataStateKind.Restoring
                ? TransferError.SessionUnavailable : TransferError.PersistenceUnavailable;
        }
    }

    public bool CanOperate => !_disposed && _registration.CanMutate;
    internal string Status => _registration.State.Kind.ToString().ToLowerInvariant();
    private void Clear()
    { _session = null; _retained = TransferSidecar.Empty(); _engine?.Restore(_retained); _resetUi(); }
    private void Observe(LifecycleEvent e)
    {
        if (_disposed || e.Session == null) return;
        if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            if (_session == e.Session.Id || _lifecycle.CurrentSession?.Id == e.Session.Id) Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose(); _disposed = true; _lifecycle.Changed -= Observe; Clear();
    }
}
