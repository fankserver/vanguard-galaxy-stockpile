using System.Collections.Generic;
using VGModAPI;

namespace VGStockpile.Data;

internal static class JumpDistances
{
    internal static IReadOnlyDictionary<string, int> ComputeFromCurrent()
        => ComputeFrom(ModApi.Services.Travel.CurrentLocation?.SystemId);

    // Counts are unweighted gate hops, not access-aware route eligibility.
    internal static IReadOnlyDictionary<string, int> ComputeFrom(string? systemId)
    {
        if (string.IsNullOrEmpty(systemId)) return new Dictionary<string, int>();
        return ComputeFrom(systemId, ModApi.Services.Navigation);
    }
    internal static IReadOnlyDictionary<string, int> ComputeFrom(string systemId, INavigationService navigation)
    {
        if (navigation.SessionId is not System.Guid session) return new Dictionary<string, int>();
        var result = navigation.GetJumpCounts(session, systemId);
        return result.Status == NavigationStatus.Succeeded ? result.Hops : new Dictionary<string, int>();
    }
}
