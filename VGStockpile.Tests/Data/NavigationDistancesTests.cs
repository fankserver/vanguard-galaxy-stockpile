using System;
using System.Collections.Generic;
using VGModAPI;
using VGStockpile.Data;
using Xunit;

namespace VGStockpile.Tests.Data;
public sealed class NavigationDistancesTests
{
    private sealed class Navigation : INavigation
    {
        public NavigationStatus Status = NavigationStatus.Succeeded;
        public int Calls;
        public JumpCountsResult GetJumpCounts(string from)
        { Assert.Equal("origin", from); Calls++; return new(Status, new Dictionary<string, int> { ["origin"] = 0, ["target"] = 3 }); }
        public JumpCountResult GetJumpCount(string from, string to) => throw new NotSupportedException();
        public NavigationStationsResult GetStations(bool visitedOnly = true) => throw new NotSupportedException();
        public NavigationStatus FocusPoi(string id) => throw new NotSupportedException();
        public NavigationStatus FocusWorldSite(WorldSiteReference reference) => throw new NotSupportedException();
    }

    /// <summary>An ended game must not answer for a replacement; it reports no distances at all.</summary>
    private sealed class Game : IGame
    {
        public bool IsActive { get; set; } = true;
        public INavigation Navigation { get; } = new Navigation();
        public IInventories Inventories => throw new NotSupportedException();
        public IStory Story => throw new NotSupportedException();
        public IBars Bars => throw new NotSupportedException();
    }

    [Fact]
    public void UsesOneBulkApiReadAndDoesNotTreatRefusalAsDistanceData()
    {
        var service = new Navigation();
        Assert.Equal(3, JumpDistances.ComputeFrom("origin", service)["target"]); Assert.Equal(1, service.Calls);
        service.Status = NavigationStatus.NotReady; Assert.Empty(JumpDistances.ComputeFrom("origin", service));
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public void ReadsNothingWithoutAnActiveCapturedGame()
    {
        var game = new Game();
        Assert.Equal(3, JumpDistances.ComputeFrom("origin", game)["target"]);
        game.IsActive = false;
        Assert.Empty(JumpDistances.ComputeFrom("origin", game));
        Assert.Empty(JumpDistances.ComputeFrom("origin", (IGame?)null));
        Assert.Equal(1, ((Navigation)game.Navigation).Calls);
    }
}
