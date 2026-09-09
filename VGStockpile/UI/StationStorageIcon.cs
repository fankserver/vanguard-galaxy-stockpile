using System;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VGStockpile.UI;

internal sealed class StationStorageIcon : MonoBehaviour
{
    // SkillIcons1_31 at atlas rect (463, 793). Multiple Sprite instances
    // share this name in the runtime registry; disambiguate by rect.
    private const string SpriteName  = "SkillIcons1_31";
    private const int    SpriteRectX = 463;
    private const int    SpriteRectY = 793;

    private Image           _iconImg     = null!;
    private TextMeshProUGUI _fallbackTxt = null!;
    private ManualLogSource _log         = null!;
    private float           _nextRetry   = 0f;
    private bool            _resolved    = false;

    public static StationStorageIcon Create(
        RectTransform hudRoot,
        Action onClick,
        float rightPadding,
        float topPadding,
        ManualLogSource log)
    {
        var go = new GameObject("VGStockpile.Icon",
            typeof(RectTransform), typeof(Image), typeof(Button),
            typeof(StationStorageIcon));
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
        // a white box that would otherwise flash before the sprite loads.
        iconImg.color = new Color(1f, 1f, 1f, 0f);

        var fbGo = new GameObject("Fallback", typeof(RectTransform), typeof(TextMeshProUGUI));
        var fbRt = (RectTransform)fbGo.transform;
        fbRt.SetParent(rt, worldPositionStays: false);
        fbRt.anchorMin = Vector2.zero; fbRt.anchorMax = Vector2.one;
        fbRt.offsetMin = Vector2.zero; fbRt.offsetMax = Vector2.zero;
        var lbl = fbGo.GetComponent<TextMeshProUGUI>();
        lbl.text      = "STK";
        lbl.alignment = TextAlignmentOptions.Center;
        lbl.fontSize  = 14f;
        lbl.fontStyle = FontStyles.Bold;
        lbl.raycastTarget = false;

        var icon = go.GetComponent<StationStorageIcon>();
        icon._iconImg     = iconImg;
        icon._fallbackTxt = lbl;
        icon._log         = log;
        icon.TryResolveSprite();

        var button = go.GetComponent<Button>();
        button.onClick.AddListener(() => onClick());
        return icon;
    }

    private void Update()
    {
        if (_resolved) return;
        if (Time.unscaledTime < _nextRetry) return;
        _nextRetry = Time.unscaledTime + 1f;
        TryResolveSprite();
    }

    private void TryResolveSprite()
    {
        var sprite = SpriteLookup.FindByNameAndRect(SpriteName, SpriteRectX, SpriteRectY);
        if (sprite is null) return;

        _iconImg.sprite = sprite;
        _iconImg.color  = Color.white;
        _fallbackTxt.gameObject.SetActive(false);
        _resolved = true;
    }
}
