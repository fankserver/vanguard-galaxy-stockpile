using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
using VGStockpile.Transfers.Engine;

namespace VGStockpile.Transfers.Persistence;

internal sealed class TransferLifecycle : ITransferPersistence
{
    private readonly ILifecycleService _api;

    private readonly TransferEngine? _engine;
    private readonly ITransferStore _store;
    private readonly Action<int> _disabledPending;
    private readonly Action _resetUi;
    private readonly Action<string> _warn;

    private readonly Dictionary<Guid, TransferSidecar> _saves = new();
    private Guid? _ready;
    private bool _disposed;
    private bool _writeFault;
    private bool _restoreFault;

    internal TransferLifecycle(ILifecycleService api, TransferEngine? engine, ITransferStore store,
        Action<int> disabledPending, Action resetUi, Action<string> warn)
    {
        _api = api;

        _engine = engine; _store = store; _disabledPending = disabledPending; _resetUi = resetUi; _warn = warn;
        if (engine != null)
        {
            engine.OperationAllowed = () => CanOperate;
            engine.QueryAllowed = () => CanInspect;
            engine.UnavailableReason = () => _writeFault || _restoreFault ? TransferError.PersistenceUnavailable : TransferError.SessionUnavailable;
        }
        Clear();
        api.Changed += Observe;
        if (api.CurrentSession is { } current && IsCurrentReady(current.Id)) Restore(current);
    }

    internal static bool IsCompatible(Version version, ILifecycleService? api) => version.Major == 0 && version.Minor == 2
        && api != null && api.SessionTracking.Availability.IsAvailable && api.SaveOutcomes.Availability.IsAvailable;

    public bool CanOperate => CanInspect && !_writeFault;

    private bool CanInspect => !_disposed && _api.SessionTracking.Availability.IsAvailable
        && _api.SaveOutcomes.Availability.IsAvailable && _ready.HasValue && _saves.Count == 0
        && !_api.IsDispatchingCallbacks && _api.CurrentSession is { } current
        && current.Id == _ready && current.Phase == SessionPhase.GameplayInitialized;

    private bool IsCurrentReady(Guid id) => _api.CurrentSession is { } s && s.Id == id
        && (s.Phase == SessionPhase.PlayerReady || s.Phase == SessionPhase.GameplayInitialized);

    private void Clear()
    {
        _ready = null; _writeFault = false; _restoreFault = false; _saves.Clear();
        _engine?.Restore(TransferSidecar.Empty());
        _resetUi();
    }

    private void Restore(SessionSnapshot session)
    {
        Clear();
        try
        {
            var state = session.SavePath == null ? TransferSidecar.Empty() : _store.Load(SavePathResolver.Sidecar(session.SavePath));
            if (!IsCurrentReady(session.Id)) return;
            _engine?.Restore(state);
            _ready = session.Id;
            if (_engine == null && state.Items.Count > 0) _disabledPending(state.Items.Count);
        }
        catch (Exception ex)
        {
            if (!IsCurrentReady(session.Id)) return;
            _restoreFault = true;
            _warn("Transfer restore failed; transfers disabled for this attempt: " + ex);
        }
    }

    private void Observe(LifecycleEvent e)
    {
        if (_disposed) return;
        switch (e.Kind)
        {
            case LifecycleEventKind.SessionStarting:
            case LifecycleEventKind.SessionInvalidated:
            case LifecycleEventKind.SessionStartFailed:
                if (_ready == e.Session?.Id || _api.CurrentSession?.Id == e.Session?.Id) Clear();
                break;
            case LifecycleEventKind.PlayerReady:
                if (e.Session != null && _ready != e.Session.Id && IsCurrentReady(e.Session.Id)) Restore(e.Session);
                break;
            case LifecycleEventKind.SaveStarted:
                if (_engine != null && e.OperationId is { } operation && e.Session != null
                    && _ready == e.Session.Id && IsCurrentReady(e.Session.Id))
                    _saves[operation] = _engine.Snapshot();
                break;
            case LifecycleEventKind.SaveSucceeded:
            case LifecycleEventKind.SaveFailed:
            case LifecycleEventKind.SaveSkipped:
                FinishSave(e);
                break;
        }
    }

    private void FinishSave(LifecycleEvent e)
    {
        if (e.OperationId is not { } operation || !_saves.TryGetValue(operation, out var snapshot)) return;
        try
        {
            if (e.Kind != LifecycleEventKind.SaveSucceeded || e.Destination == null || e.Session == null
                || _ready != e.Session.Id || !IsCurrentReady(e.Session.Id)) return;
            _store.Save(SavePathResolver.Sidecar(e.Destination), snapshot);
            _writeFault = false;
        }
        catch (Exception ex)
        {
            if (e.Session == null || _ready != e.Session.Id || !IsCurrentReady(e.Session.Id)) return;
            _writeFault = true;
            _warn("Transfer sidecar write failed after vanilla success; mutations paused until a successful save: " + ex);
        }
        finally { _saves.Remove(operation); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.Changed -= Observe;
        Clear();
    }
}
