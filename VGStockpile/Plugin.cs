using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using VGModAPI;
using VGModAPI.Unity;
using Source.Galaxy;
using Source.Galaxy.POI;
using UnityEngine;
using VGStockpile.Config;
using VGStockpile.Data;
using VGStockpile.Locate;
using VGStockpile.Transfers;
using VGStockpile.Transfers.Engine;
using VGStockpile.Transfers.Persistence;
using VGStockpile.UI;
using VGStockpile.UI.Refinery;
using VGStockpile.UI.Transfers;

namespace VGStockpile;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency(ModApi.PluginId, "0.2.8")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid    = "vgstockpile";
    public const string PluginName    = "Stockpile";
    public const string PluginVersion = "0.9.0";

    internal static Plugin          Instance { get; private set; } = null!;
    internal static ManualLogSource Log      { get; private set; } = null!;

    internal StockpileConfig         Cfg     { get; private set; } = null!;
    internal MaterialCatalog         Catalog { get; private set; } = null!;
    internal StationStorageReader    Reader  { get; private set; } = null!;
    internal StationLocator          Locator { get; private set; } = null!;
    internal StorageGridBuilder      Builder { get; private set; } = null!;
    internal RefineryJobReader       RefineryReader  { get; private set; } = null!;
    internal RefineryJobsBuilder     RefineryBuilder { get; private set; } = null!;

    private StationStorageIcon?      _icon;
    private StationStorageWindow?    _window;
    private RefineryJobsIcon?        _refineryIcon;
    private RefineryJobsWindow?      _refineryWindow;
    private IGameplayUiService?      _gameplayUi;
    private GameplayUiContainer?     _container;

    internal TransferEngine?          _engine;
    internal MaterialStorageMutator?  _mutator;
    internal CreditsMutator?          _credits;
    internal StationContextAdapter?   _ctxAdapter;

    private ITransferPersistence? _lifecycle;
    private string? _lastPersistenceStatus;
    private TransferEngineDriver? _driver;
    private int _pendingWarning;

    private void Awake()
    {
        Instance = this;
        Log      = Logger;

        Cfg     = new StockpileConfig(Config);
        Catalog = new MaterialCatalog();
        Reader  = new StationStorageReader(Log);
        Locator = new StationLocator(Log);
        Builder = new StorageGridBuilder(Catalog);
        RefineryReader  = new RefineryJobReader(Log);
        RefineryBuilder = new RefineryJobsBuilder(Catalog);

        var api = ModApi.Services.Lifecycle;
        if (!Chainloader.PluginInfos.TryGetValue(ModApi.PluginId, out var apiPlugin)
            || !TransferLifecycle.IsCompatible(apiPlugin.Metadata.Version, api))
        {
            enabled = false;
            Log.LogError("Requires VGModAPI 0.2.x with lifecycle/save capabilities; Stockpile disabled without touching sidecars.");
            return;
        }
        try
        {
            var store = new JsonTransferStore(msg => Log.LogWarning(msg));
            if (Cfg.TransfersEnabled.Value)
            {
                var tcfg = Cfg.ToTransferConfig();
                _mutator = new MaterialStorageMutator(Log);
                _credits = new CreditsMutator(Log);
                _ctxAdapter = new StationContextAdapter(Log);
                var queue = new TransferQueue(tcfg.MaxConcurrent);
                _engine = new TransferEngine(queue, _mutator, _credits, tcfg) { OperationAllowed = () => false };
                _driver = TransferEngineDriver.Attach(gameObject, _engine,
                    onCompleted: req =>
                    {
                        var dest = ResolveStationName(req.DestStationGuid);
                        var lines = string.Join(", ", req.Manifest.Select(l => $"{l.Quantity} {l.ItemIdentifier}"));
                        Notifications.Toast($"Transfer complete at {dest}: {lines}.");
                        RefreshWindowIfOpen();
                    });
                Log.LogInfo("VGStockpile transfers enabled.");
            }

            // The API owns gameplay UI readiness; subscribe first, then read Current so a
            // host that already exists is not missed. Notifications never replay.
            _gameplayUi = ModApi.Services.GameplayUi;
            _gameplayUi.Changed += OnGameplayUiChanged;
            var coordinated = Config.Bind("Persistence", "UseApiSaveData", true, "Use API-managed transfer saves. Experimental; disable to use legacy save files.").Value;
            var importLegacy = Config.Bind("Persistence", "ImportLegacySidecars", false, "Read existing transfer files when no API-managed transfer data exists. Sources remain untouched; matching the old queue to this game save is your choice.").Value;
            _lifecycle = coordinated
                ? new CoordinatedTransfers(api!, ModApi.Services.SaveData, _engine, importLegacy,
                    count => _pendingWarning = count, ResetTransferUi, message => Log.LogWarning(message))
                : new TransferLifecycle(api!, _engine, store, count => _pendingWarning = count, ResetTransferUi, message => Log.LogWarning(message));
            AttachUi(_gameplayUi.Current);
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; waiting for the gameplay UI host. API remains experimental.");
        }
        catch (System.Exception error)
        {
            enabled = false;
            OnDestroy();
            Log.LogError($"Stockpile initialization failed: {error}");
        }
    }

    private void ResetTransferUi()
    {
        _pendingWarning = 0;
        Locator.BindSession(null);
        if (_window) _window.Hide();
        if (_refineryWindow) _refineryWindow.Hide();
    }

    private void Update()
    {
        if (_lifecycle is CoordinatedTransfers coordinated && coordinated.Status != _lastPersistenceStatus)
        {
            _lastPersistenceStatus = coordinated.Status;
            Log.LogInfo("Transfer save-data status: " + _lastPersistenceStatus);
        }
        if (_pendingWarning <= 0 || !_icon || _lifecycle?.CanOperate != true) return;
        var count = _pendingWarning;
        _pendingWarning = 0;
        Notifications.Toast($"VGStockpile transfers disabled — {count} pending transfers will not deliver until re-enabled.");
    }

    private void OnDestroy()
    {
        if (_gameplayUi != null) _gameplayUi.Changed -= OnGameplayUiChanged;
        _lifecycle?.Dispose();
        if (_driver) Destroy(_driver);
        DetachUi();
    }

    /// <summary>Teardown arrives before readiness, so a replaced host rebuilds rather than resurrects.</summary>
    private void OnGameplayUiChanged(GameplayUiChange change)
    {
        if (change.Previous != null && ReferenceEquals(change.Previous, _container?.Host)) DetachUi();
        if (change.Current != null) AttachUi(change.Current);
    }

    /// <summary>Drops this mod's content and its container lease; the API destroys the owned root.</summary>
    private void DetachUi()
    {
        if (_window) Destroy(_window.gameObject);
        if (_refineryWindow) Destroy(_refineryWindow.gameObject);
        if (_icon) Destroy(_icon.gameObject);
        if (_refineryIcon) Destroy(_refineryIcon.gameObject);
        _window = null; _refineryWindow = null; _icon = null; _refineryIcon = null;
        _container?.Dispose();
        _container = null;
    }

    private void AttachUi(GameplayUiSnapshot? host)
    {
        if (host is null || _gameplayUi is null) return;
        if (ReferenceEquals(host, _container?.Host)) return;
        var status = _gameplayUi.CreateContainer(host, PluginGuid, "windows", out var container);
        if (status != GameplayUiContainerStatus.Created || container is null)
        {
            // No retry, timeout or singleton lookup: another readiness notification is the only signal.
            Log.LogWarning($"Gameplay UI host refused ({status}); Stockpile UI stays detached.");
            return;
        }
        DetachUi();
        _container = container;
        var hudRoot = container.Root;
        Locator.BindSession(ModApi.Services.Navigation.SessionId);

        var clickHandler = new StationRowClickHandler(
            Locator,
            closeWindow:         () => _window?.Hide(),
            shouldCloseOnLocate: () => Cfg.CloseWindowOnLocate.Value,
            logWarning:          msg => Log.LogWarning(msg));

        var transfersEnabled = Cfg.TransfersEnabled.Value && _engine is not null;
        TransferConfig? transferCfg = transfersEnabled ? Cfg.ToTransferConfig() : null;

        _window = StationStorageWindow.Create(
            hudRoot,
            Builder,
            Catalog,
            initialActive:    () => Cfg.GetActive(),
            onActiveChanged:  active => Cfg.SetActive(active),
            onLabelClick:     snap => clickHandler.Click(snap),
            transfersEnabled: transfersEnabled,
            transferCfg:      transferCfg,
            getStationContext: transfersEnabled ? guid => BuildStationContextFor(guid) : null,
            onPullClick:      transfersEnabled ? snap => OpenTransferDialog(snap, TransferDirection.Pull)  : null,
            onPushClick:      transfersEnabled ? snap => OpenTransferDialog(snap, TransferDirection.Push) : null,
            getPending:               transfersEnabled ? () => _engine!.Pending : null,
            onCancelTransfer:         transfersEnabled ? id => CancelTransferAndRefresh(id) : null,
            onLocateByGuid:           transfersEnabled ? guid => Locator.LocateByGuid(guid) : null,
            stationDisplayNameByGuid: transfersEnabled ? ResolveStationName : null,
            initialShowEmptyRefineries:   () => Cfg.ShowEmptyRefineries.Value,
            onShowEmptyRefineriesChanged: v => Cfg.ShowEmptyRefineries.Value = v);

        _icon = StationStorageIcon.Create(
            hudRoot,
            onClick: ToggleWindow,
            rightPadding: Cfg.IconRightPadding.Value,
            topPadding:   Cfg.IconTopPadding.Value,
            log:          Log);

        _refineryWindow = RefineryJobsWindow.Create(
            hudRoot, RefineryBuilder, Catalog,
            capture: () => RefineryReader.CaptureAll(),
            onStationClick: guid =>
            {
                Locator.LocateByGuid(guid);
                if (Cfg.CloseWindowOnLocate.Value) _refineryWindow?.Hide();
            },
            log: Log);

        // Place the refinery-jobs icon to the left of the stockpile icon
        // (icons are 40px wide; +48 leaves an 8px gap), same top edge.
        _refineryIcon = RefineryJobsIcon.Create(
            hudRoot,
            onClick: ToggleRefineryWindow,
            rightPadding: Cfg.IconRightPadding.Value + 48f,
            topPadding:   Cfg.IconTopPadding.Value,
            log:          Log);

        Log.LogInfo($"VGStockpile UI attached to gameplay host {host.Id}.");
    }

    private StationContext BuildStationContextFor(string guid)
    {
        if (_ctxAdapter is null) return default;
        var data = GalaxyMapData.current;
        if (data is null) return default;
        SpaceStation? found = null;
        foreach (var poi in data.allPointsOfInterest)
        {
            if (poi is SpaceStation st && st.guid == guid)
            {
                found = st;
                break;
            }
        }
        if (found is null) return default;
        return _ctxAdapter.FromStation(found, SpaceStation.current);
    }

    private string ResolveStationName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return guid;

        // Walk all POIs directly — Reader.CaptureAll() still drops empty
        // non-refinery stations, so a reservation that drains such a source
        // would otherwise leave that station's name unresolved mid-flight.
        var data = GalaxyMapData.current;
        if (data is null) return guid;
        foreach (var poi in data.allPointsOfInterest)
        {
            if (poi is not SpaceStation st) continue;
            if (st.guid != guid) continue;
            return st.name ?? guid;
        }
        return guid;
    }

    private void OpenTransferDialog(StationStorageSnapshot snap, TransferDirection dir)
    {
        if (_engine is null || _ctxAdapter is null) return;
        // Created on player input, long after attach; the lease keeps the parent valid.
        if (_container?.IsValid != true) { Log.LogWarning("Cannot open dialog: no gameplay UI host."); return; }

        var current = SpaceStation.current;
        if (current is null)
        {
            // Defensive: row buttons gate this via EligibilityRules.CanPull/PushFrom
            // requiring IsPlayerDocked, but if a stale click slips through we
            // refuse to open the dialog and toast.
            UI.Notifications.Toast("Transfers require docking at a station.");
            return;
        }

        var allSnaps = Reader.CaptureAll();
        var currentName = current.name ?? "";

        // Source stock comes from the live snapshot list, never the clicked
        // row's window-open snapshot — otherwise the dialog could offer ore an
        // in-flight transfer has already taken (CommitTransfer would then clamp
        // it and refuse). Resolved for both directions by TransferSourceStock.
        var sourceStock = TransferSourceStock.Resolve(dir, snap, current?.guid, allSnaps);

        string fromName, toName;
        int jumpDistance;
        if (dir == TransferDirection.Pull)
        {
            fromName     = snap.StationName;
            toName       = currentName;
            jumpDistance = ComputeJumpDistance(snap.SystemGuid, current?.system?.guid);
        }
        else
        {
            fromName     = currentName;
            toName       = snap.StationName;
            jumpDistance = ComputeJumpDistance(current?.system?.guid, snap.SystemGuid);
        }

        if (jumpDistance < 0) { Notifications.Toast("Jump distance unavailable; transfer cannot be quoted."); return; }
        TransferDialog.Open(
            _container.Root,
            dir, fromName, toName,
            sourceStock, Cfg.ToTransferConfig(), jumpDistance,
            Catalog,
            onConfirmRequest: manifest => CommitTransfer(snap, dir, manifest),
            onCancel: () => Log.LogDebug($"{dir} dialog cancelled."));
    }

    private TransferDialogOutcome CommitTransfer(
        StationStorageSnapshot snap, TransferDirection dir,
        IReadOnlyList<TransferManifestLine> manifest)
    {
        if (_engine is null) return new TransferDialogOutcome(false, "Engine unavailable.");

        string sourceGuid, destGuid;
        int jumpDistance;
        var current = SpaceStation.current;
        var currentGuid = current?.guid ?? "";

        if (dir == TransferDirection.Pull)
        {
            sourceGuid   = snap.StationId;
            destGuid     = currentGuid;
            jumpDistance = ComputeJumpDistance(snap.SystemGuid, current?.system?.guid);
        }
        else
        {
            sourceGuid   = currentGuid;
            destGuid     = snap.StationId;
            jumpDistance = ComputeJumpDistance(current?.system?.guid, snap.SystemGuid);
        }

        if (string.IsNullOrEmpty(sourceGuid) || string.IsNullOrEmpty(destGuid))
            return new TransferDialogOutcome(false, "Invalid station selection.");

        if (jumpDistance < 0) return new TransferDialogOutcome(false, "Jump distance unavailable; retry when navigation is ready.");

        // Fresh source stock for re-validation.
        var live = Reader.CaptureAll();
        var liveSource = live.FirstOrDefault(s => s.StationId == sourceGuid);
        if (liveSource is null)
            return new TransferDialogOutcome(false, "Source station unavailable.");

        // Clamp manifest against live availability; if any line was clamped, refuse + banner.
        var clamped = new List<TransferManifestLine>(manifest.Count);
        var anyClamped = false;
        for (var i = 0; i < manifest.Count; i++)
        {
            var line = manifest[i];
            var avail = liveSource.Items.TryGetValue(line.ItemIdentifier, out var n) ? n : 0;
            if (avail < line.Quantity) anyClamped = true;
            var qty = avail < line.Quantity ? avail : line.Quantity;
            if (qty > 0) clamped.Add(new TransferManifestLine(line.ItemIdentifier, qty));
        }
        if (anyClamped)
            return new TransferDialogOutcome(false, $"Stock changed at {liveSource.StationName}; reduce and retry.");
        if (clamped.Count == 0)
            return new TransferDialogOutcome(false, "Nothing left to transfer.");

        var result = _engine.RequestTransfer(sourceGuid, destGuid, clamped, jumpDistance);
        if (!result.IsSuccess)
        {
            var msg = result.Error switch
            {
                TransferError.InsufficientCredits => "Insufficient credits.",
                TransferError.QueueFull           => "Transfer queue full.",
                TransferError.EmptyManifest       => "Select at least one item.",
                TransferError.PersistenceUnavailable => "Transfer persistence is unavailable. Check the log; retry saving after fixing write errors, or reload after repairing the sidecar.",
                TransferError.SessionUnavailable  => "Transfers are unavailable during loading, saving or lifecycle callbacks.",
                _                                 => "Could not queue transfer.",
            };
            return new TransferDialogOutcome(false, msg);
        }

        RefreshWindowIfOpen();   // source drained at Reserve — keep the open grid live
        return new TransferDialogOutcome(true, null);
    }

    private static int ComputeJumpDistance(string? fromSystemGuid, string? toSystemGuid)
    {
        if (string.IsNullOrEmpty(fromSystemGuid) || string.IsNullOrEmpty(toSystemGuid)) return -1;

        var dists = JumpDistances.ComputeFrom(fromSystemGuid);
        return dists.TryGetValue(toSystemGuid, out var d) ? d : -1;
    }

    private void RefreshWindowIfOpen()
    {
        // Unity overloads ==, so this also catches a window destroyed on a scene
        // change that a stale reference would let slip past `is null` (then
        // _window.IsOpen would throw MissingReferenceException).
        if (_window == null || !_window.IsOpen) return;
        try { _window.Refresh(Reader.CaptureAll()); }
        catch (System.Exception ex)
        {
            Log.LogError($"Failed to refresh stockpile window: {ex}");
        }
    }

    private bool CancelTransferAndRefresh(string id)
    {
        var ok = _engine!.CancelTransfer(id);
        if (ok) RefreshWindowIfOpen();   // source restored at Return
        return ok;
    }

    private void ToggleWindow()
    {
        if (_window is null) return;
        try
        {
            var snapshots = Reader.CaptureAll();
            Locator.BindSession(ModApi.Services.Navigation.SessionId);
            _window.Toggle(snapshots);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Failed to capture station storage: {ex}");
        }
    }

    private void ToggleRefineryWindow()
    {
        if (_refineryWindow is null) return;
        try
        {
            var jobs = RefineryReader.CaptureAll();
            Locator.BindSession(ModApi.Services.Navigation.SessionId);
            _refineryWindow.Toggle(jobs);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Failed to capture refinery jobs: {ex}");
        }
    }

}
