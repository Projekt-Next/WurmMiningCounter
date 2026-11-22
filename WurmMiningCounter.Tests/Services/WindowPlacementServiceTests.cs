using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public void IsValidAcceptsValidPlacement()
    {
        var service = CreateService();

        Assert.True(service.IsValid(new WindowPlacement(10, 20, 400), 320));
    }

    [Fact]
    public void IsValidWithNullPlacementThrowsArgumentNullException()
    {
        var service = CreateService();

        Assert.Throws<ArgumentNullException>(() => service.IsValid(null!, 320));
    }

    [Theory]
    [InlineData(double.NaN, 10, 400)]
    [InlineData(10, double.PositiveInfinity, 400)]
    [InlineData(10, 20, double.NegativeInfinity)]
    public void IsValidRejectsNonFiniteValues(double left, double top, double width)
    {
        var service = CreateService();

        Assert.False(service.IsValid(new WindowPlacement(left, top, width), 320));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsValidRejectsZeroAndNegativeWidth(double width)
    {
        var service = CreateService();

        Assert.False(service.IsValid(new WindowPlacement(10, 20, width), 320));
    }

    [Fact]
    public void IsValidRejectsWidthBelowMinimum()
    {
        var service = CreateService();

        Assert.False(service.IsValid(new WindowPlacement(10, 20, 319), 320));
    }

    [Fact]
    public void IsVisibleOnAnyScreenAcceptsVisiblePlacement()
    {
        var service = CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080));

        Assert.True(service.IsVisibleOnAnyScreen(new WindowPlacement(100, 100, 400), 96));
    }

    [Fact]
    public void IsVisibleOnAnyScreenUsesMinimumWindowHeightForPartialPlacement()
    {
        var service = CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080));

        Assert.True(service.IsVisibleOnAnyScreen(new WindowPlacement(-36, -48, 100), 80));
    }

    [Fact]
    public void IsVisibleOnAnyScreenRejectsPartialPlacementBelowMinimumVisibleArea()
    {
        var service = CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080));

        Assert.False(service.IsVisibleOnAnyScreen(new WindowPlacement(-37, -49, 100), 80));
    }

    [Fact]
    public void IsVisibleOnAnyScreenRejectsPlacementOutsideScreens()
    {
        var service = CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080));

        Assert.False(service.IsVisibleOnAnyScreen(new WindowPlacement(2500, 100, 400), 96));
    }

    [Fact]
    public void IsVisibleOnAnyScreenSupportsMultipleScreens()
    {
        var service = CreateService(
            new WindowPlacementScreenBounds(0, 0, 1920, 1080),
            new WindowPlacementScreenBounds(1920, 0, 1920, 1080));

        Assert.True(service.IsVisibleOnAnyScreen(new WindowPlacement(2000, 100, 400), 96));
    }

    private static WindowPlacementService CreateService(params WindowPlacementScreenBounds[] screens)
    {
        return new WindowPlacementService(new FakeScreenProvider(screens));
    }

    private sealed class FakeScreenProvider(IReadOnlyList<WindowPlacementScreenBounds> screens)
        : IWindowPlacementScreenProvider
    {
        public IReadOnlyList<WindowPlacementScreenBounds> GetScreenBounds() => screens;
    }
}
