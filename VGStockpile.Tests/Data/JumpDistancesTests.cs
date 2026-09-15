using VGStockpile.Data;
using Xunit;

namespace VGStockpile.Tests.Data;

public class JumpDistancesTests
{
    [Fact]
    public void ComputeFrom_NullStart_ReturnsEmpty()
    {
        var result = JumpDistances.ComputeFrom(null, (VGModAPI.IGame?)null);
        Assert.Empty(result);
    }
}
