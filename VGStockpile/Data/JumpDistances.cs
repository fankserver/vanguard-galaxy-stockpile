using System.Collections.Generic;
using VGModAPI;

namespace VGStockpile.Data;

internal static class JumpDistances
{
    internal static IReadOnlyDictionary<string, int> ComputeFromCurrent(IGame? game)
        => ComputeFrom(ModApi.Services.Travel.CurrentLocation?.SystemId, game);

    // Counts are unweighted gate hops, not access-aware route eligibility.
    internal static IReadOnlyDictionary<string, int> ComputeFrom(string? systemId, IGame? game)
    {
        if (string.IsNullOrEmpty(systemId) || game is not { IsActive: true }) return new Dictionary<string, int>();
        return ComputeFrom(systemId!, game.Navigation);
    }
    internal static IReadOnlyDictionary<string, int> ComputeFrom(string systemId, INavigation navigation)
    {
        var result = navigation.GetJumpCounts(systemId);
        return result.Status == NavigationStatus.Succeeded ? result.Hops : new Dictionary<string, int>();
    }
}
