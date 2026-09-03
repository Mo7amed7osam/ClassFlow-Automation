using ZoomAutoAdmit.UIAutomation.Input;
using Xunit;

namespace ZoomAutoAdmit.UIAutomation.Tests;

public class SingleClickExecutorTests
{
    [Fact]
    public void ExecutorCallsFakeMouseOnlyOnce()
    {
        var fake = new FakeMouseInput();
        var executor = new SingleClickExecutor(fake);

        Assert.True(executor.TryClick(100, 200));
        Assert.False(executor.TryClick(300, 400));
        Assert.Equal(1, fake.ClickCount);
        Assert.Equal((100, 200), fake.LastTarget);
    }

    [Fact]
    public void OneExecutorPerAttemptKeepsAdmittingEveryParticipant()
    {
        // A watcher admits one person per detection, so it must build an executor per attempt.
        // Sharing a single instance across a run - as the desktop watcher used to - clicked for
        // the first participant and then silently refused every later one, while the toast went
        // on being detected every few seconds.
        var mouse = new FakeMouseInput();
        var targets = new[] { (100, 200), (300, 400), (500, 600) };

        foreach (var (x, y) in targets)
        {
            Assert.True(new SingleClickExecutor(mouse).TryClick(x, y));
        }

        Assert.Equal(targets.Length, mouse.ClickCount);
        Assert.Equal(targets[^1], mouse.LastTarget);
    }

    private sealed class FakeMouseInput : IMouseInput
    {
        public int ClickCount { get; private set; }
        public (int X, int Y) LastTarget { get; private set; }

        public void LeftClickOncePreservingCursor(int x, int y)
        {
            ClickCount++;
            LastTarget = (x, y);
        }

        public void ScrollWheelPreservingCursor(int x, int y, int wheelDelta)
        {
        }
    }
}
