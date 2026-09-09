using System;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VGStockpile.UI.Refinery;

/// <summary>
/// HUD toggle button for the refinery-jobs window. Mirrors
/// <see cref="StationStorageIcon"/>; uses the game's "Refinery" sprite, falling
/// back to a "REF" text label until the sprite resolves.
/// </summary>
internal sealed class RefineryJobsIcon : MonoBehaviour
{
    private const string SpriteName = "Refinery";

    private Image           _iconImg     = null!;
    private TextMeshProUGUI _fallbackTxt = null!;
    private float           _nextRetry   = 0f;
    private float           _fallbackAt  = 0f;
    private bool            _resolved    = false;

    public static RefineryJobsIcon Create(
        RectTransform hudRoot,
        Action onClick,
        float rightPadding,
        float topPadding,
        ManualLogSource log)
    {
        var go = new GameObject("VGStockpile.RefineryIcon",
            typeof(RectTransform), typeof(Image), typeof(Button),
            typeof(RefineryJobsIcon));
        go.transform.SetParent(hudRoot, worldPositionStays: false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot     = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(40f, 40f);
        rt.anchoredPosition = new Vector2(-rightPadding, -topPadding);

        var bg = go.GetComponent<Image>();
        bg.color = new Color(0.10f, 0.14f, 0.20f, 0.85f);

        var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        var irt = (RectTransform)iconGo.transform;
        irt.SetParent(rt, worldPositionStays: false);
        irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(4f, 4f);
        irt.offsetMax = new Vector2(-4f, -4f);
        var iconImg = iconGo.GetComponent<Image>();
        iconImg.preserveAspect = true;
        iconImg.raycastTarget  = false;
        // Transparent until the sprite resolves — a spriteless Image renders as
        // a white box, which flashed before "Refinery" finished loading.
        iconImg.color = new Color(1f, 1f, 1f, 0f);

        var fbGo = new GameObject("Fallback", typeof(RectTransform), typeof(TextMeshProUGUI));
        var fbRt = (RectTransform)fbGo.transform;
        fbRt.SetParent(rt, worldPositionStays: false);
        fbRt.anchorMin = Vector2.zero; fbRt.anchorMax = Vector2.one;
        fbRt.offsetMin = Vector2.zero; fbRt.offsetMax = Vector2.zero;
        var lbl = fbGo.GetComponent<TextMeshProUGUI>();
        lbl.text      = "REF";
        lbl.alignment = TextAlignmentOptions.Center;
        lbl.fontSize  = 13f;
        lbl.fontStyle = FontStyles.Bold;
        lbl.raycastTarget = false;

        var icon = go.GetComponent<RefineryJobsIcon>();
        icon._iconImg     = iconImg;
        icon._fallbackTxt = lbl;
        // Hide the "REF" text during the normal sprite-load window; only reveal
        // it if "Refinery" still hasn't loaded after the grace period (its bundle
        // loads a beat after the HUD, unlike the always-present stockpile sprite).
        lbl.gameObject.SetActive(false);
        icon._fallbackAt = Time.unscaledTime + 2f;
        icon.TryResolveSprite();

        go.GetComponent<Button>().onClick.AddListener(() => onClick());
        return icon;
    }

    private void Update()
    {
        if (_resolved) return;

        if (Time.unscaledTime >= _nextRetry)
        {
            // Fast retries during the brief load window; back off afterwards so a
            // never-resolving sprite doesn't spam the expensive Resources scan.
            _nextRetry = Time.unscaledTime + (Time.unscaledTime < _fallbackAt ? 0.2f : 2f);
            TryResolveSprite();
            if (_resolved) return;
        }

        // Only show the "REF" text if the sprite genuinely failed to load in time.
        if (Time.unscaledTime >= _fallbackAt && !_fallbackTxt.gameObject.activeSelf)
            _fallbackTxt.gameObject.SetActive(true);
    }

    private void TryResolveSprite()
    {
        var sprite = SpriteLookup.FindByName(SpriteName);
        if (sprite is null) return;

        _iconImg.sprite = sprite;
        _iconImg.color  = Color.white;
        _fallbackTxt.gameObject.SetActive(false);
        _resolved = true;
    }
}
