using System;
using VGModAPI;

namespace VGStockpile.Tests.Transfers.Engine;

internal sealed class TestServiceStatus : IServiceStatus
{
    public ServiceAvailability Availability { get; set; } = ServiceAvailability.Available;
    public event Action<ServiceAvailability>? AvailabilityChanged { add { } remove { } }
}
