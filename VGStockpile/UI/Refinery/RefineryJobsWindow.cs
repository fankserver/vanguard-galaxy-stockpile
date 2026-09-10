using System;
using System.Collections.Generic;
using Behaviour.UI.Tooltip;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGStockpile.Data;
using VGStockpile.UI.Transfers;

namespace VGStockpile.UI.Refinery;

/// <summary>
/// Toggleable HUD window listing every active refinery job across all stations.
/// Pure observer; mirrors <see cref="StationStorageWindow"/>'s lifecycle. While
/// visible it re-captures jobs on a short timer so progress/ETA stay live.
/// </summary>
internal sealed class RefineryJobsWindow : MonoBehaviour
{
    private const float RefreshSeconds = 1f;

    private RefineryJobsBuilder _builder = null!;
    private MaterialCatalog     _catalog = null!;
    private Func<IReadOnlyList<RefineryJobSnapshot>> _capture = null!;
    private Action<string>?     _onStationClick;
    private ManualLogSource     _log = null!;

    private RectTransform _root    = null!;
    private Transform     _content = null!;
    private ScrollRect    _scroll  = null!;
    private GameObject    _empty   = null!;
    private float         _nextRefresh;

    // Persistent per-slot widgets. A refresh that keeps the same stations and
    // slot-counts reconfigures these in place (job <-> available) instead of
    // rebuilding — so a finishing job doesn't yank the scroll position.
    private readonly List<SlotHandle> _slots = new();
    private IReadOnlyList<RefineryStationGroup> _lastGroups = System.Array.Empty<RefineryStationGroup>();

    private struct SlotHandle
    {
        public Image            IconImg;
        public ItemTooltipSource IconTip;
        public TMP_Text         NameTmp;
        public ItemTooltipSource NameTip;
        public GameObject       ProgressGo;
        public RectTransform    Fill;
        public TMP_Text         Pct;
        public TMP_Text         Amount;
        public TMP_Text         Eta;
    }

    public static RefineryJobsWindow Create(
        RectTransform hudRoot,
        RefineryJobsBuilder builder,
        MaterialCatalog catalog,
        Func<IReadOnlyList<RefineryJobSnapshot>> capture,
        Action<string>? onStationClick,
        ManualLogSource log)
    {
        var go = new GameObject("VGRefineryJobsWindow",
            typeof(RectTransform), typeof(Image), typeof(RefineryJobsWindow));
        go.transform.SetParent(hudRoot, worldPositionStays: false);

        var w = go.GetComponent<RefineryJobsWindow>();
        w._root           = (RectTransform)go.transform;
        w._builder        = builder;
        w._catalog        = catalog;
        w._capture        = capture;
        w._onStationClick = onStationClick;
        w._log            = log;
        w.BuildLayout();

        go.SetActive(false);
        return w;
    }

    public void Show(IReadOnlyList<RefineryJobSnapshot> snapshots)
    {
        gameObject.SetActive(true);
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        RebuildRows(snapshots);
    }

    public void Hide() => gameObject.SetActive(false);

    public void Toggle(IReadOnlyList<RefineryJobSnapshot> snapshots)
    {
        if (gameObject.activeSelf) Hide();
        else Show(snapshots);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape) && TransferDialog.OpenCount == 0)
        {
            Hide();
            return;
        }

        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        try { RebuildRows(_capture()); }
        catch (Exception ex) { _log.LogError($"Refinery jobs refresh failed: {ex}"); }
    }

    // -------------------------------------------------------------------------

    private void BuildLayout()
    {
        _root.anchorMin = new Vector2(0.31f, 0.16f);
        _root.anchorMax = new Vector2(0.69f, 0.84f);
        _root.offsetMin = Vector2.zero;
        _root.offsetMax = Vector2.zero;
        GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.11f, 0.92f);

        // Header band.
        var header = new GameObject("Header", typeof(RectTransform), typeof(Image));
        var hrt = (RectTransform)header.transform;
        hrt.SetParent(_root, worldPositionStays: false);
        hrt.anchorMin = new Vector2(0f, 1f);
        hrt.anchorMax = new Vector2(1f, 1f);
        hrt.pivot     = new Vector2(0.5f, 1f);
        hrt.sizeDelta = new Vector2(0f, 40f);
        hrt.anchoredPosition = Vector2.zero;
        header.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.18f, 1f);

        var title = UiText.Label("Title", header.transform, "Refinery Jobs", 16f, FontStyles.Bold);
        var trt = (RectTransform)title.transform;
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(0f, 1f);
        trt.pivot     = new Vector2(0f, 0.5f);
        trt.sizeDelta = new Vector2(240f, 0f);
        trt.anchoredPosition = new Vector2(12f, 0f);

        var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
        var crt = (RectTransform)closeGo.transform;
        crt.SetParent(header.transform, worldPositionStays: false);
        crt.anchorMin = new Vector2(1f, 0.5f);
        crt.anchorMax = new Vector2(1f, 0.5f);
        crt.pivot     = new Vector2(1f, 0.5f);
        crt.sizeDelta = new Vector2(48f, 24f);
        crt.anchoredPosition = new Vector2(-8f, 0f);
        closeGo.GetComponent<Image>().color = new Color(0.30f, 0.10f, 0.10f, 0.85f);
        closeGo.GetComponent<Button>().onClick.AddListener(Hide);
        var clbl = UiText.Label("X", closeGo.transform, "Close", 12f, FontStyles.Bold,
            TextAlignmentOptions.Center);
        var clrt = (RectTransform)clbl.transform;
        clrt.anchorMin = Vector2.zero; clrt.anchorMax = Vector2.one;
        clrt.offsetMin = Vector2.zero; clrt.offsetMax = Vector2.zero;

        BuildColumnHeader();
        BuildBody();
        BuildEmptyState();
    }

    // Fixed column-header band below the window title. Uses the same column
    // structure as a job row so the labels line up with the cells below.
    private void BuildColumnHeader()
    {
        var colGo = new GameObject("ColumnHeader",
            typeof(RectTransform), typeof(HorizontalLayoutGroup));
        var crt = (RectTransform)colGo.transform;
        crt.SetParent(_root, worldPositionStays: false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot     = new Vector2(0.5f, 1f);
        crt.offsetMin = new Vector2(14f, -64f); // align with content (8 body + 6 viewport)
        crt.offsetMax = new Vector2(-14f, -44f);

        var hlg = colGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.padding = new RectOffset(18, 6, 0, 0); // match the row indent
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;

        ColumnLabel(colGo.transform, "", 20f, TextAlignmentOptions.Left);            // icon column
        ColumnLabel(colGo.transform, "Material", 170f, TextAlignmentOptions.Left, minWidth: 80f);
        ColumnLabel(colGo.transform, "Progress", 110f, TextAlignmentOptions.Center, minWidth: 110f, flexibleWidth: 1f);
        ColumnLabel(colGo.transform, "Amount",   70f, TextAlignmentOptions.MidlineRight);
        ColumnLabel(colGo.transform, "ETA",      64f, TextAlignmentOptions.MidlineRight);
    }

    private static void ColumnLabel(
        Transform parent, string text, float width, TextAlignmentOptions align,
        float minWidth = 0f, float flexibleWidth = 0f)
    {
        var go = UiText.Label("Col", parent, text, 11f, FontStyles.Bold, align);
        UiText.Component(go).color = new Color(0.6f, 0.68f, 0.78f);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width; le.minWidth = minWidth; le.flexibleWidth = flexibleWidth;
    }

    private void BuildBody()
    {
        var bodyGo = new GameObject("Body", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        var brt = (RectTransform)bodyGo.transform;
        brt.SetParent(_root, worldPositionStays: false);
        brt.anchorMin = new Vector2(0f, 0f);
        brt.anchorMax = new Vector2(1f, 1f);
        brt.offsetMin = new Vector2(8f, 8f);
        brt.offsetMax = new Vector2(-8f, -64f); // below 40px header + 20px column header
        bodyGo.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.09f, 0.6f);

        var scroll = bodyGo.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical   = true;

        // RectMask2D clips rectangularly in-shader — no Image/extra draw call.
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        var vrt = (RectTransform)viewport.transform;
        vrt.SetParent(brt, worldPositionStays: false);
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new Vector2(6f, 6f);
        vrt.offsetMax = new Vector2(-6f, -6f);

        var content = new GameObject("Content",
            typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        var crt = (RectTransform)content.transform;
        crt.SetParent(vrt, worldPositionStays: false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot     = new Vector2(0.5f, 1f);
        // Zero the offsets so width == viewport. A code-created RectTransform
        // keeps a default sizeDelta of (100,100); without this the content is
        // 100px wider than the viewport and shifts ~50px left, so the mask
        // clips the left of every header. ContentSizeFitter drives height.
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;
        var vlg = content.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 3f;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.padding = new RectOffset(2, 2, 2, 2);
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Slim vertical scrollbar (auto-hides when everything fits).
        var sbGo = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        sbGo.transform.SetParent(brt, worldPositionStays: false);
        sbGo.GetComponent<Image>().color = new Color(0.08f, 0.10f, 0.12f, 0.6f);
        var sbrt = (RectTransform)sbGo.transform;
        sbrt.anchorMin = new Vector2(1f, 0f);
        sbrt.anchorMax = new Vector2(1f, 1f);
        sbrt.pivot     = new Vector2(1f, 0.5f);
        sbrt.offsetMin = new Vector2(-8f, 6f);
        sbrt.offsetMax = new Vector2(0f, -6f);

        var sbArea = new GameObject("SlidingArea", typeof(RectTransform));
        sbArea.transform.SetParent(sbGo.transform, worldPositionStays: false);
        var sart = (RectTransform)sbArea.transform;
        sart.anchorMin = Vector2.zero; sart.anchorMax = Vector2.one;
        sart.offsetMin = new Vector2(1f, 1f); sart.offsetMax = new Vector2(-1f, -1f);

        var sbHandle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        sbHandle.transform.SetParent(sbArea.transform, worldPositionStays: false);
        sbHandle.GetComponent<Image>().color = new Color(0.35f, 0.40f, 0.46f, 0.85f);
        var shrt = (RectTransform)sbHandle.transform;
        shrt.anchorMin = Vector2.zero; shrt.anchorMax = Vector2.one;
        shrt.offsetMin = Vector2.zero; shrt.offsetMax = Vector2.zero;

        var sbComp = sbGo.GetComponent<Scrollbar>();
        sbComp.direction     = Scrollbar.Direction.BottomToTop;
        sbComp.targetGraphic = sbHandle.GetComponent<Image>();
        sbComp.handleRect    = shrt;
        scroll.verticalScrollbar = sbComp;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        // Make room for the scrollbar so it doesn't overlap row content.
        vrt.offsetMax = new Vector2(-12f, -6f);

        scroll.viewport = vrt;
        scroll.content  = crt;
        _scroll  = scroll;
        _content = crt;
    }

    private void BuildEmptyState()
    {
        var go = UiText.Label("Empty", _root, "No active refinery jobs.", 14f,
            FontStyles.Normal, TextAlignmentOptions.Center);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(0f, 0f);
        rt.offsetMax = new Vector2(0f, -40f);
        _empty = go;
        _empty.SetActive(false);
    }

    private void RebuildRows(IReadOnlyList<RefineryJobSnapshot> snapshots)
    {
        var groups = _builder.Build(snapshots);

        // Flatten to one entry per visible slot: a job, or null for an empty
        // ("available") queue slot, padding each station up to its slot count.
        var flat = new List<RefineryJobRow?>();
        foreach (var g in groups)
        {
            var rows = SlotCount(g);
            for (var i = 0; i < rows; i++)
                flat.Add(i < g.Jobs.Count ? g.Jobs[i] : (RefineryJobRow?)null);
        }

        // Fast path: same stations + same slot-counts as last time -> just
        // reconfigure the existing slot widgets (job <-> available) in place.
        // A finishing job keeps the slot, so the layout/scroll never jump.
        if (StructureEquals(groups) && _slots.Count == flat.Count)
        {
            for (var i = 0; i < flat.Count; i++)
            {
                if (flat[i] is { } row) ConfigureJob(_slots[i], row);
                else                    ConfigureAvailable(_slots[i]);
            }
            return;
        }

        // Structure changed (station appeared/left, or slot-count changed) -> rebuild.
        _lastGroups = groups;
        var scrollPos = _scroll.verticalNormalizedPosition;
        _slots.Clear();

        // Detach before Destroy: Destroy is deferred to end of frame, so the
        // old children would still count in the layout during the rebuild +
        // ForceRebuildLayoutImmediate below, distorting size/scroll.
        for (var i = _content.childCount - 1; i >= 0; i--)
        {
            var child = _content.GetChild(i);
            child.SetParent(null, worldPositionStays: false);
            Destroy(child.gameObject);
        }

        _empty.SetActive(groups.Count == 0);

        foreach (var group in groups)
        {
            BuildStationHeader(group);
            var rows = SlotCount(group);
            for (var i = 0; i < rows; i++)
            {
                var slot = BuildSlot();
                _slots.Add(slot);
                if (i < group.Jobs.Count) ConfigureJob(slot, group.Jobs[i]);
                else                      ConfigureAvailable(slot);
            }
        }

        // Restore scroll position after the content's height is recomputed.
        LayoutRebuilder.ForceRebuildLayoutImmediate(_content as RectTransform);
        _scroll.verticalNormalizedPosition = Mathf.Clamp01(scrollPos);
    }

    // Visible rows for a station: its queue slots, but never fewer than its
    // active jobs (jobs queued under a higher skill cap can outlast it).
    private static int SlotCount(RefineryStationGroup g) => Mathf.Max(g.Jobs.Count, g.MaxJobs);

    // True when the displayed structure is unchanged from last refresh: same
    // stations in order, each with the same slot count. The job-vs-available
    // mix within a station can change without a rebuild. Allocation-free so it
    // can run every second. (The string-signature alternative allocated.)
    private bool StructureEquals(IReadOnlyList<RefineryStationGroup> groups)
    {
        if (_lastGroups.Count != groups.Count) return false;
        for (var i = 0; i < groups.Count; i++)
        {
            if (_lastGroups[i].StationId != groups[i].StationId ||
                SlotCount(_lastGroups[i]) != SlotCount(groups[i]))
                return false;
        }
        return true;
    }

    private void ConfigureJob(SlotHandle h, RefineryJobRow row)
    {
        var s = row.Snapshot;

        h.NameTmp.text      = row.MaterialName;
        h.NameTmp.fontStyle = FontStyles.Normal;
        h.NameTmp.color     = new Color(0.92f, 0.94f, 0.98f, 1f);

        var sprite = _catalog.Icon(s.MaterialId);
        if (sprite != null) { h.IconImg.sprite = sprite; h.IconImg.color = Color.white; }
        else                { h.IconImg.sprite = null;   h.IconImg.color = new Color(0.3f, 0.3f, 0.3f, 0.6f); }

        h.ProgressGo.SetActive(true);
        var f = Mathf.Clamp01(s.ProgressFraction);
        h.Fill.anchorMax = new Vector2(f, 1f);
        h.Pct.text    = $"{Mathf.RoundToInt(f * 100f)}%";
        h.Amount.text = $"{CompactNumber.Format(s.RemainingAmount)}/{CompactNumber.Format(s.InitialAmount)}";
        h.Eta.text    = FormatEta(s.EtaSeconds);

        var type = _catalog.GetItemType(s.MaterialId);
        ConfigureTooltip(h.IconTip, type);
        ConfigureTooltip(h.NameTip, type);
    }

    private void ConfigureAvailable(SlotHandle h)
    {
        h.NameTmp.text      = "- available -";
        h.NameTmp.fontStyle = FontStyles.Italic;
        h.NameTmp.color     = new Color(0.5f, 0.55f, 0.62f, 0.7f);

        h.IconImg.sprite = null;
        h.IconImg.color  = new Color(1f, 1f, 1f, 0f); // hidden

        h.ProgressGo.SetActive(false);
        h.Amount.text = "";
        h.Eta.text    = "";
        h.IconTip.enabled = false;
        h.NameTip.enabled = false;
    }

    private static void ConfigureTooltip(ItemTooltipSource tip, Behaviour.Item.InventoryItemType? type)
    {
        if (type is null) { tip.enabled = false; return; }
        tip.SetItem(item: type, count: 0, allowCompare: false, context: ItemTooltipContext.InInventory);
        tip.enabled = true;
    }

    private void BuildStationHeader(RefineryStationGroup group)
    {
        var headerGo = new GameObject($"Station_{group.StationId}",
            typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        headerGo.transform.SetParent(_content, worldPositionStays: false);
        headerGo.GetComponent<Image>().color = new Color(0.12f, 0.16f, 0.21f, 0.9f);
        headerGo.GetComponent<LayoutElement>().preferredHeight = 24f;
        var hlg = headerGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6f;
        hlg.padding = new RectOffset(8, 8, 0, 0);
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;

        // System name first (dimmed), like the stockpile grid's System column.
        if (!string.IsNullOrEmpty(group.SystemName))
        {
            var sysGo = UiText.Label("System", headerGo.transform, group.SystemName, 13f, FontStyles.Bold);
            var sysTmp = UiText.Component(sysGo);
            NoWrap(sysTmp);
            sysTmp.color = new Color(0.55f, 0.60f, 0.68f, 1f);
            var sysLe = sysGo.AddComponent<LayoutElement>();
            sysLe.preferredWidth = 110f; sysLe.minWidth = 40f; sysLe.flexibleWidth = 0f;
        }

        // Faction icon immediately before the station name — it's the owner of
        // the station, not the system, so it sits with the station not the sector.
        var facGo = new GameObject("Faction", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        facGo.transform.SetParent(headerGo.transform, worldPositionStays: false);
        var facLe = facGo.GetComponent<LayoutElement>();
        facLe.preferredWidth = 18f; facLe.preferredHeight = 18f; facLe.flexibleWidth = 0f;
        var facImg = facGo.GetComponent<Image>();
        facImg.preserveAspect = true; facImg.raycastTarget = false;
        var facSprite = _catalog.FactionIcon(group.FactionId);
        if (facSprite != null) { facImg.sprite = facSprite; facImg.color = Color.white; }
        else                   { facImg.color = new Color(0.3f, 0.3f, 0.3f, 0.4f); }

        // Label as a sized HLG child, mirroring the job rows (which render their
        // text fine) rather than a stretched anchor (which clipped the name).
        var label = UiText.Label("Label", headerGo.transform, group.StationName, 13f, FontStyles.Bold);
        var tmp = UiText.Component(label);
        NoWrap(tmp);
        var labelLe = label.AddComponent<LayoutElement>();
        labelLe.preferredWidth = 240f; labelLe.minWidth = 60f; labelLe.flexibleWidth = 1f;

        // Clickable: locate the station on the map, like the stockpile grid.
        if (_onStationClick is not null)
        {
            tmp.color = new Color(0.80f, 0.87f, 1f, 1f); // hint of clickability
            var btn = headerGo.AddComponent<Button>();
            var guid = group.StationId;
            btn.onClick.AddListener(() => _onStationClick!(guid));
        }
    }

    // Builds an empty slot widget (full job-row structure). Content is set by
    // ConfigureJob / ConfigureAvailable so the same widget can be reused as a
    // job changes to an available slot without being recreated.
    private SlotHandle BuildSlot()
    {
        var rowGo = new GameObject("Slot",
            typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowGo.transform.SetParent(_content, worldPositionStays: false);
        rowGo.GetComponent<LayoutElement>().preferredHeight = 26f;
        var hlg = rowGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.padding = new RectOffset(18, 6, 0, 0); // indent under the station header

        // Material icon (+ hover tooltip target).
        var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        iconGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
        var iconLe = iconGo.GetComponent<LayoutElement>();
        iconLe.preferredWidth = 20f; iconLe.preferredHeight = 20f; iconLe.flexibleWidth = 0f;
        var iconImg = iconGo.GetComponent<Image>();
        iconImg.preserveAspect = true; iconImg.raycastTarget = true;
        var iconTip = iconGo.AddComponent<ItemTooltipSource>();

        // Material name (+ hover tooltip target).
        var nameGo = UiText.Label("Name", rowGo.transform, "", 12f);
        var nameTmp = UiText.Component(nameGo);
        NoWrap(nameTmp);
        nameTmp.raycastTarget = true;
        var nameLe = nameGo.AddComponent<LayoutElement>();
        nameLe.preferredWidth = 170f; nameLe.minWidth = 80f; nameLe.flexibleWidth = 0f;
        var nameTip = nameGo.AddComponent<ItemTooltipSource>();

        BuildProgressBar(rowGo.transform, 0f, out var progressGo, out var fillRect, out var pctTmp);

        var amtGo = UiText.Label("Amount", rowGo.transform, "", 12f,
            FontStyles.Normal, TextAlignmentOptions.MidlineRight);
        var amtTmp = UiText.Component(amtGo);
        NoWrap(amtTmp);
        var amtLe = amtGo.AddComponent<LayoutElement>();
        amtLe.preferredWidth = 70f; amtLe.flexibleWidth = 0f;

        var etaGo = UiText.Label("Eta", rowGo.transform, "", 12f,
            FontStyles.Normal, TextAlignmentOptions.MidlineRight);
        var etaTmp = UiText.Component(etaGo);
        NoWrap(etaTmp);
        var etaLe = etaGo.AddComponent<LayoutElement>();
        etaLe.preferredWidth = 64f; etaLe.flexibleWidth = 0f;

        return new SlotHandle
        {
            IconImg    = iconImg,
            IconTip    = iconTip,
            NameTmp    = nameTmp,
            NameTip    = nameTip,
            ProgressGo = progressGo,
            Fill       = fillRect,
            Pct        = pctTmp,
            Amount     = amtTmp,
            Eta        = etaTmp,
        };
    }

    // Single-line, clipped text so long names/stations don't expand the row or
    // wrap onto two lines. Truncate (not Ellipsis): the pixel16 font has no
    // ellipsis glyph, which TMP warns about and falls back from anyway.
    private static void NoWrap(TMP_Text tmp)
    {
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Truncate;
    }

    private void BuildProgressBar(Transform parent, float fraction,
        out GameObject barGoOut, out RectTransform fillRect, out TMP_Text pctTmp)
    {
        if (fraction < 0f) fraction = 0f; else if (fraction > 1f) fraction = 1f;

        var barGo = new GameObject("Progress", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        barGo.transform.SetParent(parent, worldPositionStays: false);
        var barLe = barGo.GetComponent<LayoutElement>();
        // Flexible: the progress bar absorbs the row's slack so the layout uses
        // the full window width instead of leaving an empty right margin.
        barLe.preferredWidth = 110f; barLe.minWidth = 110f; barLe.preferredHeight = 16f;
        barLe.flexibleWidth = 1f;
        barGo.GetComponent<Image>().color = new Color(0.08f, 0.10f, 0.13f, 0.95f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        var frt = (RectTransform)fill.transform;
        frt.SetParent(barGo.transform, worldPositionStays: false);
        frt.anchorMin = new Vector2(0f, 0f);
        frt.anchorMax = new Vector2(fraction, 1f);
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
        var fillImg = fill.GetComponent<Image>();
        fillImg.color = new Color(0.30f, 0.62f, 0.40f, 0.95f);
        fillImg.raycastTarget = false;

        var pctGo = UiText.Label("Pct", barGo.transform, $"{Mathf.RoundToInt(fraction * 100f)}%", 11f,
            FontStyles.Bold, TextAlignmentOptions.Center);
        var prt = (RectTransform)pctGo.transform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

        barGoOut = barGo;
        fillRect = frt;
        pctTmp   = UiText.Component(pctGo);
    }

    private static string FormatEta(float seconds)
    {
        var s = (int)System.Math.Ceiling(seconds);
        if (s < 0) s = 0;
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }
}
