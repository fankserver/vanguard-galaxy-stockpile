using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using VGModAPI;
using HarmonyLib;
using Source.Galaxy;
using Source.Galaxy.POI;
using UnityEngine;
using VGStockpile.Config;
using VGStockpile.Data;
using VGStockpile.Locate;
using VGStockpile.Patches;
using VGStockpile.Transfers;
using VGStockpile.Transfers.Engine;
using VGStockpile.Transfers.Persistence;
using VGStockpile.UI;
using VGStockpile.UI.Refinery;
using VGStockpile.UI.Transfers;

namespace VGStockpile;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency(ModApi.PluginId, "0.1.2")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid    = "vgstockpile";
    public const string PluginName    = "Stockpile";
    public const string PluginVersion = "0.7.1";

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
    private Canvas?                  _hudCanvas;
    private Harmony                  _harmony = null!;

    internal TransferEngine?          _engine;
    internal MaterialStorageMutator?  _mutator;
    internal CreditsMutator?          _credits;
    internal StationContextAdapter?   _ctxAdapter;

    private ITransferPersistence? _lifecycle;
    private string? _lastPersistenceStatus;
    private TransferEngineDriver? _driver;
    private int _pendingWarning;

    public bool IconAttached => _icon != null;

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

        var api = ModApi.Current;
        if (!Chainloader.PluginInfos.TryGetValue(ModApi.PluginId, out var apiPlugin)
            || !TransferLifecycle.IsCompatible(apiPlugin.Metadata.Version, api))
        {
            enabled = false;
            Log.LogError("Requires VGModAPI 0.1.2+ within 0.1.x with lifecycle/save capabilities; Stockpile disabled without touching sidecars.");
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

            // HUD readiness remains tied to the inspected SidePanel.Start boundary.
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(SidePanelReadyPatch));
            var coordinated = Config.Bind("Persistence", "UseApiSaveData", true, "Use API-managed transfer saves. Experimental; disable to use legacy save files.").Value;
            var importLegacy = Config.Bind("Persistence", "ImportLegacySidecars", false, "Read existing transfer files when no API-managed transfer data exists. Sources remain untouched; matching the old queue to this game save is your choice.").Value;
            _lifecycle = coordinated
                ? new CoordinatedTransfers(api!, ModApi.Persistence ?? throw new System.InvalidOperationException("API-managed saves unavailable. Enable [Persistence] Enabled in vgmodapi.cfg and check API errors, or set [Persistence] UseApiSaveData = false in vgstockpile.cfg for legacy saves."), _engine, importLegacy,
                    count => _pendingWarning = count, ResetTransferUi, message => Log.LogWarning(message))
                : new TransferLifecycle(api!, _engine, store, count => _pendingWarning = count, ResetTransferUi, message => Log.LogWarning(message));
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; waiting for SidePanel. API remains experimental.");
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
        _lifecycle?.Dispose();
        if (_driver) Destroy(_driver);
        if (_window) Destroy(_window.gameObject);
        if (_refineryWindow) Destroy(_refineryWindow.gameObject);
        if (_icon) Destroy(_icon.gameObject);
        if (_refineryIcon) Destroy(_refineryIcon.gameObject);
        _harmony?.UnpatchSelf();
    }

    internal void AttachIcon(Canvas hudCanvas)
    {
        if (_icon != null) return;
        _hudCanvas = hudCanvas;

        var clickHandler = new StationRowClickHandler(
            Locator,
            closeWindow:         () => _window?.Hide(),
            shouldCloseOnLocate: () => Cfg.CloseWindowOnLocate.Value,
            logWarning:          msg => Log.LogWarning(msg));

        var transfersEnabled = Cfg.TransfersEnabled.Value && _engine is not null;
        TransferConfig? transferCfg = transfersEnabled ? Cfg.ToTransferConfig() : null;

        _window = StationStorageWindow.Create(
            hudCanvas,
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
            hudCanvas,
            onClick: ToggleWindow,
            rightPadding: Cfg.IconRightPadding.Value,
            topPadding:   Cfg.IconTopPadding.Value,
            log:          Log);

        _refineryWindow = RefineryJobsWindow.Create(
            hudCanvas, RefineryBuilder, Catalog,
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
            hudCanvas,
            onClick: ToggleRefineryWindow,
            rightPadding: Cfg.IconRightPadding.Value + 48f,
            topPadding:   Cfg.IconTopPadding.Value,
            log:          Log);

        Log.LogInfo($"VGStockpile icon attached to canvas '{hudCanvas.name}'.");
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
        if (_hudCanvas is null) { Log.LogWarning("Cannot open dialog: no HUD canvas."); return; }

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

        TransferDialog.Open(
            _hudCanvas.transform,
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
        if (string.IsNullOrEmpty(fromSystemGuid) || string.IsNullOrEmpty(toSystemGuid)) return 0;
        if (fromSystemGuid == toSystemGuid) return 0;

        var data = GalaxyMapData.current;
        if (data is null) return 0;

        SystemMapData? from = null;
        foreach (var s in data.allSystems)
        {
            if (s?.guid == fromSystemGuid) { from = s; break; }
        }
        if (from is null) return 0;

        var dists = JumpDistances.ComputeFrom(from);
        return dists.TryGetValue(toSystemGuid, out var d) ? d : 0;
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
            _refineryWindow.Toggle(jobs);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Failed to capture refinery jobs: {ex}");
        }
    }

}
