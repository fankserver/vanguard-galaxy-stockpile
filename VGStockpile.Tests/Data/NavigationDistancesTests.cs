using System;
using System.Collections.Generic;
using VGModAPI;
using VGStockpile.Data;
using Xunit;

namespace VGStockpile.Tests.Data;
public sealed class NavigationDistancesTests
{
    private sealed class Navigation : INavigationService
    {
        public ServiceAvailability Availability => ServiceAvailability.Available;
        public event Action<ServiceAvailability>? AvailabilityChanged { add { } remove { } }
        public Guid? SessionId { get; set; } = Guid.NewGuid();
        public NavigationStatus Status = NavigationStatus.Succeeded;
        public int Calls;
        public JumpCountsResult GetJumpCounts(Guid session, string from)
        { Assert.Equal(SessionId, session); Assert.Equal("origin", from); Calls++; return new(Status, new Dictionary<string, int> { ["origin"] = 0, ["target"] = 3 }); }
        public JumpCountResult GetJumpCount(Guid session, string from, string to) => throw new NotSupportedException();
        public NavigationStationsResult GetStations(Guid session, bool visitedOnly = true) => throw new NotSupportedException();
        public NavigationStatus FocusPoi(Guid session, string id) => throw new NotSupportedException();
        public NavigationStatus FocusWorldSite(Guid session, WorldSiteReference reference) => throw new NotSupportedException();
    }
    [Fact]
    public void UsesOneBulkApiReadAndDoesNotTreatRefusalAsDistanceData()
    {
        var service = new Navigation();
        Assert.Equal(3, JumpDistances.ComputeFrom("origin", service)["target"]); Assert.Equal(1, service.Calls);
        service.Status = NavigationStatus.NotReady; Assert.Empty(JumpDistances.ComputeFrom("origin", service));
        service.SessionId = null; Assert.Empty(JumpDistances.ComputeFrom("origin", service)); Assert.Equal(2, service.Calls);
    }
}
