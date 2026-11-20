using Toolbox.Remote;

namespace Toolbox.Tests;

/// <summary>How a remote desktop is placed in its pane (RdpPane.FitFor).</summary>
public class RemoteDesktopTests
{
    [Fact]
    public void ADesktopMadeForThePaneIsShownPixelForPixel()
    {
        // The core rounds the width down to a multiple of 4 and the height to an even number.
        Assert.Equal((1900, 1322), RdpPane.FitFor(1900, 1322, 1903, 1323));
        Assert.Equal((1900, 1322), RdpPane.FitFor(1900, 1322, 1900, 1322));
    }

    [Fact]
    public void ASmallerDesktopIsNotMagnified()
    {
        Assert.Equal((640, 480), RdpPane.FitFor(640, 480, 1903, 1323));
    }

    [Fact]
    public void ALargerDesktopIsShrunkAndKeepsItsShape()
    {
        Assert.Equal((950, 661), RdpPane.FitFor(1900, 1322, 950, 1322));
        Assert.Equal((800, 600), RdpPane.FitFor(1600, 1200, 800, 900));
        // Narrower than the 200 pixels a desktop is at least.
        Assert.Equal((150, 150), RdpPane.FitFor(200, 200, 150, 400));
    }

    [Fact]
    public void ThePictureNeverNeedsMoreRoomThanThePaneHas()
    {
        foreach (var (width, height) in new[] { (1903, 1323), (777, 1291), (641, 333), (2559, 1401), (3, 5) })
        {
            var (w, h) = RdpPane.FitFor(1900, 1322, width, height);
            Assert.InRange(w, 1, width);
            Assert.InRange(h, 1, height);
        }
    }

    [Fact]
    public void NothingToPlaceWithoutADesktopOrAPane()
    {
        Assert.Equal((0, 0), RdpPane.FitFor(0, 0, 800, 600));
        Assert.Equal((0, 0), RdpPane.FitFor(800, 600, 0, 0));
    }
}
