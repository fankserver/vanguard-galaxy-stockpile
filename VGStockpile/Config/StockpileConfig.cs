using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using VGStockpile.Data;
using VGStockpile.Transfers;

namespace VGStockpile.Config;

internal sealed class StockpileConfig
{
    public ConfigEntry<string> ActiveCategories    { get; }
    public ConfigEntry<bool>   CloseWindowOnLocate { get; }
    public ConfigEntry<bool>   ShowEmptyRefineries { get; }

    // [Transfers] section
    public ConfigEntry<bool>  TransfersEnabled       { get; }
    public ConfigEntry<bool>  PushEnabled            { get; }
    public ConfigEntry<int>   MaxConcurrent          { get; }
    public ConfigEntry<int>   QuantityStepSmall      { get; }
    public ConfigEntry<int>   QuantityStepLarge      { get; }
    public ConfigEntry<int>   ShiftMultiplier        { get; }
    public ConfigEntry<int>   FeeBase                { get; }
    public ConfigEntry<int>   FeePerUnit             { get; }
    public ConfigEntry<int>   FeePerJump             { get; }
    public ConfigEntry<float> FeePerUnitPerJump      { get; }
    public ConfigEntry<float> EtaBaseSeconds         { get; }
    public ConfigEntry<float> EtaPerJumpSeconds      { get; }
    public ConfigEntry<float> EtaMinSeconds          { get; }
    public ConfigEntry<float> EtaMaxSeconds          { get; }
    public ConfigEntry<bool>  PushRequiresPeaceful   { get; }
    public ConfigEntry<bool>  PushRequiresRefinery   { get; }

    // Categories visible by default: everything except Ores.
    private static readonly MaterialCategory[] DefaultActive =
    {
        MaterialCategory.RefinedCanister,
        MaterialCategory.RefinedGoods,
        MaterialCategory.Crystal,
        MaterialCategory.TradeGoods,
        MaterialCategory.Salvage,
        MaterialCategory.Other,
    };

    public StockpileConfig(ConfigFile cfg)
    {
        ActiveCategories = cfg.Bind(
            "UI", "ActiveCategories",
            string.Join(",", DefaultActive.Select(c => c.ToString())),
            "Comma-separated list of MaterialCategory names visible in the grid. " +
            "Toggling a filter button updates this. Valid values: " +
            "Ore, RefinedCanister, RefinedGoods, Crystal, TradeGoods, Salvage, Other. " +
            "(Legacy 'Refined' is auto-migrated to RefinedCanister + RefinedGoods.)");
        CloseWindowOnLocate = cfg.Bind("UI", "CloseWindowOnLocate", true,
            "When clicking a station label, close the stockpile window after focusing the map.");
        ShowEmptyRefineries = cfg.Bind("UI", "ShowEmptyRefineries", false,
            "Also list stations that have a refinery even when they hold no materials, " +
            "so they can be used as push targets. Toggled by a header button. Off by default.");

        TransfersEnabled = cfg.Bind("Transfers", "Enabled", false,
            "Master gate. When false, VGStockpile is pure observer (default).");
        PushEnabled = cfg.Bind("Transfers", "PushEnabled", false,
            "Render Push buttons on grid rows. Off by default.");
        MaxConcurrent = cfg.Bind("Transfers", "MaxConcurrent", 3,
            "Max in-flight transfers (0 = unlimited).");
        QuantityStepSmall = cfg.Bind("Transfers", "QuantityStepSmall", 1, "Smaller quantity step button.");
        QuantityStepLarge = cfg.Bind("Transfers", "QuantityStepLarge", 20, "Larger quantity step button.");
        ShiftMultiplier   = cfg.Bind("Transfers", "ShiftMultiplier",   5,  "Shift-held multiplier on both step buttons.");
        FeeBase           = cfg.Bind("Transfers", "FeeBase",           100, "Flat fee component (credits).");
        FeePerUnit        = cfg.Bind("Transfers", "FeePerUnit",        1,   "Per-unit fee component.");
        FeePerJump        = cfg.Bind("Transfers", "FeePerJump",        50,  "Per-jump fee component.");
        FeePerUnitPerJump = cfg.Bind("Transfers", "FeePerUnitPerJump", 0.5f,"Cross fee = units * jumps * this.");
        EtaBaseSeconds    = cfg.Bind("Transfers", "EtaBaseSeconds",    30f, "Flat ETA component (seconds, in-game time).");
        EtaPerJumpSeconds = cfg.Bind("Transfers", "EtaPerJumpSeconds", 20f, "Per-jump ETA component.");
        EtaMinSeconds     = cfg.Bind("Transfers", "EtaMinSeconds",     15f, "ETA lower clamp.");
        EtaMaxSeconds     = cfg.Bind("Transfers", "EtaMaxSeconds",     1800f, "ETA upper clamp.");
        PushRequiresPeaceful = cfg.Bind("Transfers", "PushRequiresPeaceful", true,
            "When true, only peaceful destinations accept push.");
        PushRequiresRefinery = cfg.Bind("Transfers", "PushRequiresRefinery", true,
            "When true, only refinery-equipped destinations accept push.");
    }

    public HashSet<MaterialCategory> GetActive()
    {
        var set = new HashSet<MaterialCategory>();
        foreach (var part in ActiveCategories.Value.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            // Migration: the old "Refined" bucket maps to both new buckets.
            if (string.Equals(trimmed, "Refined", System.StringComparison.OrdinalIgnoreCase))
            {
                set.Add(MaterialCategory.RefinedCanister);
                set.Add(MaterialCategory.RefinedGoods);
                continue;
            }
            if (System.Enum.TryParse<MaterialCategory>(trimmed, ignoreCase: true, out var cat))
                set.Add(cat);
        }
        return set;
    }

    public void SetActive(IEnumerable<MaterialCategory> active)
    {
        ActiveCategories.Value = string.Join(",", active.OrderBy(c => (int)c).Select(c => c.ToString()));
    }

    public TransferConfig ToTransferConfig() => new(
        Enabled: TransfersEnabled.Value, PushEnabled: PushEnabled.Value,
        MaxConcurrent: MaxConcurrent.Value,
        QuantityStepSmall: QuantityStepSmall.Value,
        QuantityStepLarge: QuantityStepLarge.Value,
        ShiftMultiplier:   ShiftMultiplier.Value,
        FeeBase: FeeBase.Value, FeePerUnit: FeePerUnit.Value,
        FeePerJump: FeePerJump.Value, FeePerUnitPerJump: FeePerUnitPerJump.Value,
        EtaBaseSeconds: EtaBaseSeconds.Value, EtaPerJumpSeconds: EtaPerJumpSeconds.Value,
        EtaMinSeconds: EtaMinSeconds.Value,   EtaMaxSeconds: EtaMaxSeconds.Value,
        PushRequiresPeaceful: PushRequiresPeaceful.Value,
        PushRequiresRefinery: PushRequiresRefinery.Value);
}
