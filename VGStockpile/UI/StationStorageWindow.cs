using System;
using System.Collections.Generic;
using Behaviour.UI.Tooltip;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGStockpile.Data;
using VGStockpile.Transfers;
using VGStockpile.UI.Transfers;
using IReadOnlyTransferList = System.Collections.Generic.IReadOnlyList<VGStockpile.Transfers.TransferRequest>;

namespace VGStockpile.UI;

internal sealed class StationStorageWindow : MonoBehaviour
{
    private RectTransform _root        = null!;
    private RectTransform _gridContent = null!;
    private RectTransform _filterStrip = null!;
    private RectTransform _scrollRect  = null!;
    private GameObject    _emptyState  = null!;

    private StorageGridBuilder              _builder         = null!;
    private MaterialCatalog                 _catalog         = null!;
    private Func<HashSet<MaterialCategory>> _initialActive   = null!;
    private Action<HashSet<MaterialCategory>> _onActiveChanged = null!;
    private Action<StationStorageSnapshot>  _onLabelClick    = null!;

    // Transfer row buttons (optional — null when transfers are disabled).
    private bool                             _transfersEnabled;
    private TransferConfig?                  _transferCfg;
    private Func<string, StationContext>?    _getStationContext;
    private Action<StationStorageSnapshot>?  _onPullClick;
    private Action<StationStorageSnapshot>?  _onPushClick;

    // In-flight strip (optional — null when transfers are disabled).
    private Func<IReadOnlyTransferList>?     _getPending;
    private Func<string, bool>?              _onCancelTransfer;
    private Action<string>?                  _onLocateByGuid;
    private Func<string, string>?            _stationDisplayNameByGuid;

    private readonly HashSet<MaterialCategory> _active = new();
    private readonly Dictionary<MaterialCategory, Image> _categoryButtons = new();

    // "Show empty refineries" row-filter toggle (independent of the category
    // column filters). _scroll is kept so Refresh() can preserve scroll position.
    private bool _showEmptyRefineries;
    private Action<bool>? _onShowEmptyRefineriesChanged;
    private Image? _refineryToggleBg;
    private Image? _refineryToggleIcon;
    private bool   _refineryIconResolved;
    private ScrollRect? _scroll;

    private IReadOnlyList<StationStorageSnapshot> _currentSnapshots =
        Array.Empty<StationStorageSnapshot>();
    private IReadOnlyDictionary<string, int> _jumpDistances =
        new Dictionary<string, int>();

    // Color scheme matches VGHangar's filter buttons.
    private static readonly Color BtnActive   = new(0.30f, 0.40f, 0.50f, 0.85f);
    private static readonly Color BtnInactive = new(0.20f, 0.20f, 0.20f, 0.80f);

    public static StationStorageWindow Create(
        RectTransform hudRoot,
        StorageGridBuilder builder,
        MaterialCatalog catalog,
        Func<HashSet<MaterialCategory>> initialActive,
        Action<HashSet<MaterialCategory>> onActiveChanged,
        Action<StationStorageSnapshot> onLabelClick,
        bool transfersEnabled = false,
        TransferConfig? transferCfg = null,
        Func<string, StationContext>? getStationContext = null,
        Action<StationStorageSnapshot>? onPullClick = null,
        Action<StationStorageSnapshot>? onPushClick = null,
        Func<IReadOnlyTransferList>? getPending = null,
        Func<string, bool>? onCancelTransfer = null,
        Action<string>? onLocateByGuid = null,
        Func<string, string>? stationDisplayNameByGuid = null,
        Func<bool>? initialShowEmptyRefineries = null,
        Action<bool>? onShowEmptyRefineriesChanged = null)
    {
        var go = new GameObject(
            "VGStockpile.Window",
            typeof(RectTransform), typeof(CanvasGroup), typeof(Image),
            typeof(StationStorageWindow));
        go.transform.SetParent(hudRoot, worldPositionStays: false);

        var w = go.GetComponent<StationStorageWindow>();
        w._root               = (RectTransform)go.transform;
        w._builder            = builder;
        w._catalog            = catalog;
        w._initialActive      = initialActive;
        w._onActiveChanged    = onActiveChanged;
        w._onLabelClick       = onLabelClick;
        w._transfersEnabled        = transfersEnabled;
        w._transferCfg             = transferCfg;
        w._getStationContext       = getStationContext;
        w._onPullClick             = onPullClick;
        w._onPushClick             = onPushClick;
        w._getPending              = getPending;
        w._onCancelTransfer        = onCancelTransfer;
        w._onLocateByGuid          = onLocateByGuid;
        w._stationDisplayNameByGuid = stationDisplayNameByGuid;
        w._showEmptyRefineries          = initialShowEmptyRefineries?.Invoke() ?? false;
        w._onShowEmptyRefineriesChanged = onShowEmptyRefineriesChanged;
        foreach (var c in initialActive()) w._active.Add(c);
        w.BuildLayout();
        w.Hide();
        return w;
    }

    public void Show(IReadOnlyList<StationStorageSnapshot> snapshots)
    {
        _currentSnapshots = snapshots;
        _jumpDistances    = JumpDistances.ComputeFromCurrent();
        gameObject.SetActive(true);
        Render();
    }

    public void Hide() => gameObject.SetActive(false);

    public void Toggle(IReadOnlyList<StationStorageSnapshot> snapshots)
    {
        if (gameObject.activeSelf) Hide();
        else Show(snapshots);
    }

    public bool IsOpen => gameObject.activeSelf;

    // Re-render an already-open window with fresh data, preserving scroll
    // position. Used after a transfer changes station storage so the grid does
    // not show stale rows until the user reopens it.
    public void Refresh(IReadOnlyList<StationStorageSnapshot> snapshots)
    {
        if (!gameObject.activeSelf) return;
        _currentSnapshots = snapshots;
        _jumpDistances    = JumpDistances.ComputeFromCurrent();

        var h = _scroll != null ? _scroll.horizontalNormalizedPosition : 0f;
        var v = _scroll != null ? _scroll.verticalNormalizedPosition   : 1f;
        Render();
        if (_scroll != null)
        {
            // Content was rebuilt; force a layout pass before restoring the
            // normalized scroll position or it clamps against a stale size.
            Canvas.ForceUpdateCanvases();
            _scroll.horizontalNormalizedPosition = h;
            _scroll.verticalNormalizedPosition   = v;
        }
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;
        // If a transfer dialog is open, it consumes ESC (closes itself first).
        if (TransferDialog.OpenCount > 0) return;
        Hide();
    }

    private void BuildLayout()
    {
        _root.anchorMin = new Vector2(0.15f, 0.10f);
        _root.anchorMax = new Vector2(0.85f, 0.90f);
        _root.offsetMin = Vector2.zero;
        _root.offsetMax = Vector2.zero;

        var bg = GetComponent<Image>();
        bg.color = new Color(0.06f, 0.08f, 0.11f, 0.92f);

        BuildHeader();
        BuildGrid();
        BuildEmptyState();

        if (_transfersEnabled && _getPending is not null
            && _onCancelTransfer is not null && _onLocateByGuid is not null
            && _stationDisplayNameByGuid is not null)
        {
            BuildInFlightStrip();
        }
    }

    private void BuildInFlightStrip()
    {
        // Shrink scroll area bottom to make room for the 36px footer strip
        // (footer sits at y=4 with 36px height = 40px, plus 4px gap = 44px).
        _scrollRect.offsetMin = new Vector2(8f, 44f);

        // Footer container anchored to the bottom of the window, below the scroll area.
        var footerGo = new GameObject("InFlightFooter",
            typeof(RectTransform), typeof(Image));
        var frt = (RectTransform)footerGo.transform;
        frt.SetParent(_root, worldPositionStays: false);
        frt.anchorMin = new Vector2(0f, 0f);
        frt.anchorMax = new Vector2(1f, 0f);
        frt.pivot     = new Vector2(0.5f, 0f);
        frt.sizeDelta = new Vector2(0f, 36f);
        frt.anchoredPosition = new Vector2(0f, 4f);
        footerGo.GetComponent<Image>().color = new Color(0.08f, 0.10f, 0.14f, 0.85f);

        InFlightStrip.Attach(
            frt,
            _getPending!,
            _onCancelTransfer!,
            _onLocateByGuid!,
            _stationDisplayNameByGuid!);
    }

    private void BuildHeader()
    {
        var header = new GameObject("Header",
            typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)header.transform;
        rt.SetParent(_root, worldPositionStays: false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, 40f);
        rt.anchoredPosition = Vector2.zero;
        header.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.18f, 1f);

        var title = MakeLabel("Title", header.transform, "Station Stockpiles", 16f, FontStyles.Bold);
        var trt = (RectTransform)title.transform;
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(0f, 1f);
        trt.pivot     = new Vector2(0f, 0.5f);
        trt.sizeDelta = new Vector2(220f, 0f);
        trt.anchoredPosition = new Vector2(12f, 0f);

        // Filter strip: row of category-toggle buttons centered to the right
        // of the title.
        var stripGo = new GameObject("FilterStrip",
            typeof(RectTransform), typeof(HorizontalLayoutGroup));
        var strt = (RectTransform)stripGo.transform;
        strt.SetParent(header.transform, worldPositionStays: false);
        strt.anchorMin = new Vector2(1f, 0.5f);
        strt.anchorMax = new Vector2(1f, 0.5f);
        strt.pivot     = new Vector2(1f, 0.5f);
        // Fits the refinery toggle + a 10px gap + 6 category buttons, with slack.
        strt.sizeDelta = new Vector2(300f, 32f);
        strt.anchoredPosition = new Vector2(-64f, 0f);
        var hlg = stripGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6f;
        hlg.childAlignment = TextAnchor.MiddleRight;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;
        _filterStrip = strt;
        BuildRefineryToggle();   // leftmost in the right-aligned cluster
        BuildCategoryButtons();

        var closeGo = new GameObject("Close",
            typeof(RectTransform), typeof(Image), typeof(Button));
        var crt = (RectTransform)closeGo.transform;
        crt.SetParent(header.transform, worldPositionStays: false);
        crt.anchorMin = new Vector2(1f, 0.5f);
        crt.anchorMax = new Vector2(1f, 0.5f);
        crt.pivot     = new Vector2(1f, 0.5f);
        crt.sizeDelta = new Vector2(48f, 24f);
        crt.anchoredPosition = new Vector2(-8f, 0f);
        closeGo.GetComponent<Image>().color = new Color(0.30f, 0.10f, 0.10f, 0.85f);
        closeGo.GetComponent<Button>().onClick.AddListener(Hide);
        var clbl = MakeLabel("X", closeGo.transform, "Close", 12f, FontStyles.Bold);
        var clrt = (RectTransform)clbl.transform;
        clrt.anchorMin = Vector2.zero; clrt.anchorMax = Vector2.one;
        clrt.offsetMin = Vector2.zero; clrt.offsetMax = Vector2.zero;
        var clblText = clbl.GetComponent<TextMeshProUGUI>();
        clblText.alignment = TextAlignmentOptions.Center;
        clblText.color     = Color.white;
    }

    private void BuildCategoryButtons()
    {
        foreach (var (cat, sprName, rectX, rectY, label) in MaterialCategoryDisplay.Filterable)
        {
            var btnGo = new GameObject($"Filter_{cat}",
                typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(LayoutElement));
            btnGo.transform.SetParent(_filterStrip, worldPositionStays: false);
            var le = btnGo.GetComponent<LayoutElement>();
            le.preferredWidth  = 30f;
            le.preferredHeight = 30f;
            le.flexibleWidth   = 0f;

            var bg = btnGo.GetComponent<Image>();
            bg.color = _active.Contains(cat) ? BtnActive : BtnInactive;
            _categoryButtons[cat] = bg;

            var btn = btnGo.GetComponent<Button>();
            var captured = cat;
            btn.onClick.AddListener(() => OnFilterClicked(captured));

            // Inner icon Image — fits inside with a small inset so the
            // background tint is visible as a border.
            var iconGo = new GameObject("Icon",
                typeof(RectTransform), typeof(Image));
            var irt = (RectTransform)iconGo.transform;
            irt.SetParent(btnGo.transform, worldPositionStays: false);
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
            irt.offsetMin = new Vector2(3f, 3f);
            irt.offsetMax = new Vector2(-3f, -3f);
            var iconImg = iconGo.GetComponent<Image>();
            iconImg.preserveAspect = true;
            iconImg.raycastTarget  = false;
            var sprite = SpriteLookup.FindByNameAndRect(sprName, rectX, rectY)
                         ?? SpriteLookup.FindByName(sprName);
            if (sprite != null) { iconImg.sprite = sprite; iconImg.color = Color.white; }
            else                { iconImg.color  = new Color(0.5f, 0.5f, 0.5f, 0.6f); }

            // Plain TooltipSource (not ItemTooltipSource) — vanilla treats
            // these as named hover regions with a title + body.
            var tip = btnGo.AddComponent<TooltipSource>();
            tip.Title    = label;
            tip.BodyText = $"Toggle visibility of {label} in the grid.";
        }
    }

    private void OnFilterClicked(MaterialCategory cat)
    {
        if (_active.Contains(cat)) _active.Remove(cat);
        else                       _active.Add(cat);

        if (_categoryButtons.TryGetValue(cat, out var bg))
            bg.color = _active.Contains(cat) ? BtnActive : BtnInactive;

        _onActiveChanged(new HashSet<MaterialCategory>(_active));
        Render();
    }

    private void BuildRefineryToggle()
    {
        var btnGo = new GameObject("Filter_Refineries",
            typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        btnGo.transform.SetParent(_filterStrip, worldPositionStays: false);
        var le = btnGo.GetComponent<LayoutElement>();
        le.preferredWidth  = 30f;
        le.preferredHeight = 30f;
        le.flexibleWidth   = 0f;

        var bg = btnGo.GetComponent<Image>();
        bg.color = _showEmptyRefineries ? BtnActive : BtnInactive;
        _refineryToggleBg = bg;

        btnGo.GetComponent<Button>().onClick.AddListener(OnRefineryToggleClicked);

        var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        var irt = (RectTransform)iconGo.transform;
        irt.SetParent(btnGo.transform, worldPositionStays: false);
        irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(3f, 3f);
        irt.offsetMax = new Vector2(-3f, -3f);
        var iconImg = iconGo.GetComponent<Image>();
        iconImg.preserveAspect = true;
        iconImg.raycastTarget  = false;
        // Reuse the refinery-jobs window's icon (the game's "Refinery" sprite).
        // That bundle loads a beat after the HUD, so the sprite can be null at
        // header-build time — start transparent and re-resolve on each Render
        // until it sticks, mirroring the refinery-jobs HUD icon's retry.
        iconImg.color = new Color(1f, 1f, 1f, 0f);
        _refineryToggleIcon = iconImg;
        TryResolveRefineryIcon();

        var tip = btnGo.AddComponent<TooltipSource>();
        tip.Title    = "Show empty refineries";
        tip.BodyText = "Also list stations that have a refinery even when they hold no " +
                       "materials, so you can push ore to them. Off by default.";

        // Small gap so this row-filter reads as separate from the category
        // column-filter chips that follow it.
        var spacerGo = new GameObject("FilterSpacer",
            typeof(RectTransform), typeof(LayoutElement));
        spacerGo.transform.SetParent(_filterStrip, worldPositionStays: false);
        var spLe = spacerGo.GetComponent<LayoutElement>();
        spLe.preferredWidth = 10f;
        spLe.flexibleWidth  = 0f;
    }

    private void OnRefineryToggleClicked()
    {
        _showEmptyRefineries = !_showEmptyRefineries;
        if (_refineryToggleBg != null)
            _refineryToggleBg.color = _showEmptyRefineries ? BtnActive : BtnInactive;
        _onShowEmptyRefineriesChanged?.Invoke(_showEmptyRefineries);
        Render();
    }

    // The toggle shares the refinery-jobs window's "Refinery" sprite, whose
    // bundle loads shortly after the HUD; resolve lazily until it's available.
    private void TryResolveRefineryIcon()
    {
        if (_refineryIconResolved || _refineryToggleIcon == null) return;
        var sprite = SpriteLookup.FindByName("Refinery");
        if (sprite == null) return;
        _refineryToggleIcon.sprite = sprite;
        _refineryToggleIcon.color  = Color.white;
        _refineryIconResolved = true;
    }

    private void BuildGrid()
    {
        var scroll = new GameObject("Scroll",
            typeof(RectTransform), typeof(ScrollRect), typeof(Image));
        var srt = (RectTransform)scroll.transform;
        srt.SetParent(_root, worldPositionStays: false);
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(1f, 1f);
        srt.offsetMin = new Vector2(8f, 8f);
        srt.offsetMax = new Vector2(-8f, -48f);
        _scrollRect = srt;
        scroll.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.30f);

        var viewport = new GameObject("Viewport",
            typeof(RectTransform), typeof(Image), typeof(RectMask2D));
        var vrt = (RectTransform)viewport.transform;
        vrt.SetParent(scroll.transform, worldPositionStays: false);
        vrt.anchorMin = new Vector2(0f, 0f);
        vrt.anchorMax = new Vector2(1f, 1f);
        vrt.offsetMin = Vector2.zero;
        vrt.offsetMax = Vector2.zero;
        viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

        var content = new GameObject("Content",
            typeof(RectTransform), typeof(VerticalLayoutGroup),
            typeof(ContentSizeFitter));
        var crt = (RectTransform)content.transform;
        crt.SetParent(viewport.transform, worldPositionStays: false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(0f, 1f);
        crt.pivot     = new Vector2(0f, 1f);
        crt.anchoredPosition = Vector2.zero;

        var fitter = content.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
        var vlg = content.GetComponent<VerticalLayoutGroup>();
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth  = false;
        vlg.spacing = 2f;

        var sr = scroll.GetComponent<ScrollRect>();
        sr.viewport   = vrt;
        sr.content    = crt;
        sr.horizontal = true;
        sr.vertical   = true;
        _scroll       = sr;

        _gridContent = crt;
    }

    private void BuildEmptyState()
    {
        var go = MakeLabel("Empty", _root, "No stations with stored materials.",
            14f, FontStyles.Italic);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(0f, 0f);
        rt.offsetMax = new Vector2(0f, -48f);
        var lbl = go.GetComponent<TextMeshProUGUI>();
        lbl.alignment = TextAlignmentOptions.Center;
        _emptyState = go;
        _emptyState.SetActive(false);
    }

    private void Render()
    {
        TryResolveRefineryIcon();   // "Refinery" sprite may load after header build

        // Detach before Destroy: Destroy is deferred to end-of-frame, so the
        // doomed rows would otherwise still be counted by the immediate layout
        // pass in Refresh() (Canvas.ForceUpdateCanvases) and skew scroll restore.
        for (int i = _gridContent.childCount - 1; i >= 0; i--)
        {
            var child = _gridContent.GetChild(i).gameObject;
            child.transform.SetParent(null, false);
            Destroy(child);
        }

        var grid = _builder.Build(_currentSnapshots, _active, _showEmptyRefineries);

        if (grid.Rows.Count == 0)
        {
            _emptyState.SetActive(true);
            return;
        }
        _emptyState.SetActive(false);

        BuildHeaderRow(grid.ColumnMaterialIds);

        foreach (var row in grid.Rows)
        {
            BuildDataRow(
                materialIds: grid.ColumnMaterialIds,
                cells: row.Cells,
                snapshot: row.Snapshot);
        }
    }

    // Leftmost (sticky) area: system + faction icon + station + jumps.
    // Sum is shared by header and data rows so the material columns line up.
    private const float SystemNameWidth  = 90f;
    private const float FactionIconWidth = 24f;
    private const float StationNameWidth = 90f;
    private const float JumpsCellWidth   = 36f;
    private const float AutoRefineCellWidth = 34f;
    private const float MaterialCellWidth = 56f;

    private void BuildHeaderRow(IReadOnlyList<string> materialIds)
    {
        var rowGo = NewRow(isHeader: true);

        // Spacer to align header columns with data rows that have transfer buttons.
        if (_transfersEnabled)
        {
            var btnSpacer = new GameObject("TransferBtnSpacer",
                typeof(RectTransform), typeof(LayoutElement));
            btnSpacer.transform.SetParent(rowGo.transform, worldPositionStays: false);
            var bsle = btnSpacer.GetComponent<LayoutElement>();
            bsle.preferredWidth = 90f;
            bsle.minWidth       = 90f;
            bsle.flexibleWidth  = 0f;
        }

        var systemHeaderGo = new GameObject("SystemHeader",
            typeof(RectTransform), typeof(LayoutElement),
            typeof(TextMeshProUGUI));
        systemHeaderGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var she = systemHeaderGo.GetComponent<LayoutElement>();
        she.preferredWidth = SystemNameWidth;
        she.flexibleWidth  = 0f;
        var systemHeaderText = systemHeaderGo.GetComponent<TextMeshProUGUI>();
        systemHeaderText.text      = "System";
        systemHeaderText.fontSize  = 12f;
        systemHeaderText.fontStyle = FontStyles.Bold;
        systemHeaderText.alignment = TextAlignmentOptions.Left;

        // 24px spacer for the faction-icon cell that sits in data rows here.
        var spacer = new GameObject("HeaderIconSpacer",
            typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var sle = spacer.GetComponent<LayoutElement>();
        sle.preferredWidth = FactionIconWidth;
        sle.flexibleWidth  = 0f;

        var stationHeaderGo = new GameObject("StationHeader",
            typeof(RectTransform), typeof(LayoutElement),
            typeof(TextMeshProUGUI));
        stationHeaderGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var lle = stationHeaderGo.GetComponent<LayoutElement>();
        lle.preferredWidth = StationNameWidth;
        lle.flexibleWidth  = 0f;
        var lblText = stationHeaderGo.GetComponent<TextMeshProUGUI>();
        lblText.text      = "Station";
        lblText.fontSize  = 12f;
        lblText.fontStyle = FontStyles.Bold;
        lblText.alignment = TextAlignmentOptions.Left;

        // Jumps column header: icon instead of a letter. Right-anchored
        // 20x20 image inside a JumpsCellWidth-wide cell. The full cell is
        // a transparent hit-target so the tooltip fires across the whole
        // 36px slot.
        var jumpsHeaderGo = new GameObject("JumpsHeader",
            typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        jumpsHeaderGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var jhle = jumpsHeaderGo.GetComponent<LayoutElement>();
        jhle.preferredWidth  = JumpsCellWidth;
        jhle.preferredHeight = 24f;
        jhle.flexibleWidth   = 0f;
        var jhHit = jumpsHeaderGo.GetComponent<Image>();
        jhHit.color = new Color(0f, 0f, 0f, 0f);

        var jhTip = jumpsHeaderGo.AddComponent<TooltipSource>();
        jhTip.Title    = "Jumps";
        jhTip.BodyText = "Number of jumpgate hops from your current system to the station's system. " +
                         "0 means same system; '-' means no jumpgate route is available.";

        var jhImgGo = new GameObject("Icon",
            typeof(RectTransform), typeof(Image));
        var jhirt = (RectTransform)jhImgGo.transform;
        jhirt.SetParent(jumpsHeaderGo.transform, worldPositionStays: false);
        jhirt.anchorMin = new Vector2(1f, 0.5f);
        jhirt.anchorMax = new Vector2(1f, 0.5f);
        jhirt.pivot     = new Vector2(1f, 0.5f);
        jhirt.sizeDelta = new Vector2(20f, 20f);
        jhirt.anchoredPosition = Vector2.zero;
        var jhImg = jhImgGo.GetComponent<Image>();
        jhImg.preserveAspect = true;
        jhImg.raycastTarget  = false;
        var jhSprite = SpriteLookup.FindByNameAndRect("Map_Poi_Location_Jumpgate", 0, 0)
                       ?? SpriteLookup.FindByName("Map_Poi_Location_Jumpgate");
        if (jhSprite != null) { jhImg.sprite = jhSprite; jhImg.color = Color.white; }
        else                  { jhImg.color  = new Color(0.3f, 0.3f, 0.3f, 0.6f); }

        // Auto-Refine column header: the refinery icon, with the whole cell a
        // transparent hit-target so the tooltip fires across the slot.
        var autoHeaderGo = new GameObject("AutoRefineHeader",
            typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        autoHeaderGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var ahle = autoHeaderGo.GetComponent<LayoutElement>();
        ahle.preferredWidth  = AutoRefineCellWidth;
        ahle.preferredHeight = 24f;
        ahle.flexibleWidth   = 0f;
        autoHeaderGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

        var ahTip = autoHeaderGo.AddComponent<TooltipSource>();
        ahTip.Title    = "Auto-Refine";
        ahTip.BodyText = "Whether this station automatically refines its stored ore. " +
                         "Blank means the station has no refinery.";

        var ahImgGo = new GameObject("Icon",
            typeof(RectTransform), typeof(Image));
        var ahirt = (RectTransform)ahImgGo.transform;
        ahirt.SetParent(autoHeaderGo.transform, worldPositionStays: false);
        ahirt.anchorMin = new Vector2(0.5f, 0.5f);
        ahirt.anchorMax = new Vector2(0.5f, 0.5f);
        ahirt.pivot     = new Vector2(0.5f, 0.5f);
        ahirt.sizeDelta = new Vector2(20f, 20f);
        ahirt.anchoredPosition = Vector2.zero;
        var ahImg = ahImgGo.GetComponent<Image>();
        ahImg.preserveAspect = true;
        ahImg.raycastTarget  = false;
        // The station nav bar's refinery tab uses the 'leadership' sprite (a
        // dome glyph) tinted warm; mirror that here. It's greyscale, so the
        // tint colourises it.
        // TODO(post-beta): replace once the game ships a final/dedicated refinery
        // icon — 'leadership' is a borrowed sprite and the tint below is an
        // eyeballed match, not the real Image.color.
        var ahSprite = SpriteLookup.FindByName("leadership");
        if (ahSprite != null) { ahImg.sprite = ahSprite; ahImg.color = new Color(0.85f, 0.45f, 0.20f); }
        else                  { ahImg.color  = new Color(0.3f, 0.3f, 0.3f, 0.6f); }

        foreach (var id in materialIds)
        {
            var cellGo = new GameObject("MaterialIconCell",
                typeof(RectTransform), typeof(LayoutElement), typeof(Image));
            cellGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
            var le = cellGo.GetComponent<LayoutElement>();
            le.preferredWidth  = MaterialCellWidth;
            le.preferredHeight = 28f;
            le.flexibleWidth   = 0f;
            var hit = cellGo.GetComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            var imgGo = new GameObject("Icon",
                typeof(RectTransform), typeof(Image));
            var irt = (RectTransform)imgGo.transform;
            irt.SetParent(cellGo.transform, worldPositionStays: false);
            irt.anchorMin = new Vector2(1f, 0.5f);
            irt.anchorMax = new Vector2(1f, 0.5f);
            irt.pivot     = new Vector2(1f, 0.5f);
            irt.sizeDelta = new Vector2(24f, 24f);
            irt.anchoredPosition = Vector2.zero;

            var img = imgGo.GetComponent<Image>();
            img.preserveAspect = true;
            img.raycastTarget  = false;
            var sprite = _catalog.Icon(id);
            if (sprite != null) { img.sprite = sprite; img.color = Color.white; }
            else                { img.color  = new Color(0.3f, 0.3f, 0.3f, 0.6f); }

            AttachItemTooltip(cellGo, id);
        }
    }

    private void BuildDataRow(
        IReadOnlyList<string> materialIds,
        IReadOnlyList<string> cells,
        StationStorageSnapshot snapshot)
    {
        var rowGo = NewRow(isHeader: false);

        // Transfer Pull/Push buttons (prepended before any content cell).
        if (_transfersEnabled && _transferCfg is not null && _getStationContext is not null)
        {
            var transferSnap = snapshot;
            TransferRowButtons.Create(
                rowGo.transform,
                _transferCfg,
                () => _getStationContext(transferSnap.StationId),
                () => _onPullClick?.Invoke(transferSnap),
                () => _onPushClick?.Invoke(transferSnap));
        }

        // System name (leftmost).
        var systemGo = new GameObject("System",
            typeof(RectTransform), typeof(LayoutElement),
            typeof(TextMeshProUGUI));
        systemGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var sysLE = systemGo.GetComponent<LayoutElement>();
        sysLE.preferredWidth = SystemNameWidth;
        sysLE.flexibleWidth  = 0f;
        var sysText = systemGo.GetComponent<TextMeshProUGUI>();
        sysText.text      = snapshot.SystemName;
        sysText.fontSize  = 12f;
        sysText.alignment = TextAlignmentOptions.Left;

        // Faction icon (between system and station).
        var iconGo = new GameObject("FactionIcon",
            typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        iconGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var iconLE = iconGo.GetComponent<LayoutElement>();
        iconLE.preferredWidth  = FactionIconWidth;
        iconLE.preferredHeight = 20f;
        iconLE.flexibleWidth   = 0f;
        var factionImg = iconGo.GetComponent<Image>();
        factionImg.preserveAspect = true;
        factionImg.raycastTarget  = false;
        var factionSprite = _catalog.FactionIcon(snapshot.FactionId);
        if (factionSprite != null)
        {
            factionImg.sprite = factionSprite;
            factionImg.color  = Color.white;
        }
        else
        {
            factionImg.color  = new Color(0.3f, 0.3f, 0.3f, 0.4f);
        }

        // Station name (clickable for locate).
        var stationGo = new GameObject("Station",
            typeof(RectTransform), typeof(LayoutElement),
            typeof(TextMeshProUGUI));
        stationGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var stLE = stationGo.GetComponent<LayoutElement>();
        stLE.preferredWidth = StationNameWidth;
        stLE.flexibleWidth  = 0f;
        var stText = stationGo.GetComponent<TextMeshProUGUI>();
        stText.text      = snapshot.StationName;
        stText.fontSize  = 12f;
        stText.alignment = TextAlignmentOptions.Left;
        stText.color     = new Color(0.78f, 0.85f, 1f, 1f);   // hint of clickability

        var btn = stationGo.AddComponent<Button>();
        var snap = snapshot;
        btn.onClick.AddListener(() => _onLabelClick(snap));

        // Jump distance cell: number of jumpgate hops from the player's
        // current system. "—" when unreachable; "0" when in the same system.
        var jumpsGo = new GameObject("Jumps",
            typeof(RectTransform), typeof(LayoutElement),
            typeof(TextMeshProUGUI));
        jumpsGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var jle = jumpsGo.GetComponent<LayoutElement>();
        jle.preferredWidth = JumpsCellWidth;
        jle.flexibleWidth  = 0f;
        var jtxt = jumpsGo.GetComponent<TextMeshProUGUI>();
        jtxt.text      = _jumpDistances.TryGetValue(snapshot.SystemGuid, out var jumps)
                            ? jumps.ToString()
                            : "-";
        jtxt.fontSize  = 12f;
        jtxt.alignment = TextAlignmentOptions.MidlineRight;

        // Auto-refine indicator: vanilla checkbox sprite (checked = on,
        // unchecked = off). Left blank when the station has no refinery.
        var autoGo = new GameObject("AutoRefine",
            typeof(RectTransform), typeof(LayoutElement));
        autoGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var autoLE = autoGo.GetComponent<LayoutElement>();
        autoLE.preferredWidth = AutoRefineCellWidth;
        autoLE.flexibleWidth  = 0f;

        if (snapshot.AutoRefine is bool autoOn)
        {
            var glyphGo = new GameObject("Checkbox",
                typeof(RectTransform), typeof(Image));
            var grt = (RectTransform)glyphGo.transform;
            grt.SetParent(autoGo.transform, worldPositionStays: false);
            grt.anchorMin = new Vector2(0.5f, 0.5f);
            grt.anchorMax = new Vector2(0.5f, 0.5f);
            grt.pivot     = new Vector2(0.5f, 0.5f);
            grt.sizeDelta = new Vector2(16f, 16f);
            grt.anchoredPosition = Vector2.zero;
            var glyphImg = glyphGo.GetComponent<Image>();
            glyphImg.preserveAspect = true;
            glyphImg.raycastTarget  = false;
            var cbSprite = SpriteLookup.FindByName(autoOn ? "Checkbox_1" : "Checkbox_0");
            if (cbSprite != null) { glyphImg.sprite = cbSprite; glyphImg.color = Color.white; }
            // Fallback if the sprite isn't loaded: a tinted square still reads
            // as on (green) vs off (grey).
            else { glyphImg.color = autoOn ? new Color(0.4f, 0.8f, 0.4f) : new Color(0.45f, 0.45f, 0.45f); }
        }

        for (int i = 0; i < materialIds.Count; i++)
        {
            var id  = materialIds[i];
            var qty = cells[i];

            var cellGo = new GameObject("Cell",
                typeof(RectTransform), typeof(LayoutElement),
                typeof(TextMeshProUGUI));
            cellGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
            var ce = cellGo.GetComponent<LayoutElement>();
            ce.preferredWidth = MaterialCellWidth;
            ce.flexibleWidth  = 0f;
            var ctxt = cellGo.GetComponent<TextMeshProUGUI>();
            ctxt.text      = qty;
            ctxt.fontSize  = 12f;
            ctxt.alignment = TextAlignmentOptions.MidlineRight;

            if (!string.IsNullOrEmpty(qty))
                AttachItemTooltip(cellGo, id);
        }
    }

    private GameObject NewRow(bool isHeader)
    {
        var rowGo = new GameObject(isHeader ? "HeaderRow" : "Row",
            typeof(RectTransform), typeof(HorizontalLayoutGroup),
            typeof(Image), typeof(LayoutElement));
        rowGo.transform.SetParent(_gridContent, worldPositionStays: false);
        rowGo.GetComponent<Image>().color = isHeader
            ? new Color(0.18f, 0.20f, 0.25f, 0.95f)
            : new Color(0.10f, 0.12f, 0.15f, 0.85f);
        rowGo.GetComponent<LayoutElement>().minHeight = isHeader ? 32f : 24f;
        var hlg = rowGo.GetComponent<HorizontalLayoutGroup>();
        hlg.childForceExpandHeight = true;
        hlg.spacing = 4f;
        hlg.padding = new RectOffset(4, 4, 2, 2);
        return rowGo;
    }

    private void AttachItemTooltip(GameObject go, string materialTypeId)
    {
        var type = _catalog.GetItemType(materialTypeId);
        if (type is null) return;
        var src = go.AddComponent<ItemTooltipSource>();
        src.SetItem(
            item:        type,
            count:       0,
            allowCompare: false,
            context:     ItemTooltipContext.InInventory);
    }

    private static GameObject MakeLabel(
        string name, Transform parent, string text, float size, FontStyles style)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, worldPositionStays: false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.text      = text;
        t.fontSize  = size;
        t.fontStyle = style;
        t.alignment = TextAlignmentOptions.MidlineLeft;
        return go;
    }
}
