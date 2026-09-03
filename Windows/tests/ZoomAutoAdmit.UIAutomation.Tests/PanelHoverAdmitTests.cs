using System.Drawing;
using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Input;
using ZoomAutoAdmit.UIAutomation.Screen;
using Xunit;

namespace ZoomAutoAdmit.UIAutomation.Tests;

public class PanelHoverAdmitTests
{
    [Fact]
    public void FinalHoverCursorRemainsInsideRow()
    {
        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1485, 230, 425, 26),
            SafeHoverPoint = (1560, 243)
        };

        var cursor = new FakeCursorController((100, 100));
        var activator = new SyntheticHoverActivator(cursor, _ => { });

        var trace = activator.Activate((1490, 150), (1560, 243));

        Assert.Equal((1560, 243), cursor.GetPosition());
        Assert.True(row.RowBounds.Contains(cursor.GetPosition().X, cursor.GetPosition().Y));
    }

    [Fact]
    public void WaitingRowHoverPathEntersInsideRowBeforeMovingToParticipantName()
    {
        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "Mohab Mohamed (Guest)",
            RowBounds = new(200, 103, 353, 42),
            TextBounds = new(239, 112, 162, 22),
            SafeHoverPoint = (271, 123)
        };

        var entry = WaitingRowHoverPath.GetEntryPoint(row);
        var cursor = new FakeCursorController((10, 10));
        var trace = new SyntheticHoverActivator(cursor, _ => { })
            .Activate(entry, ((int)row.SafeHoverPoint.X, (int)row.SafeHoverPoint.Y));

        Assert.True(row.RowBounds.Contains(entry.X, entry.Y));
        Assert.True(entry.X < row.SafeHoverPoint.X);
        Assert.Equal(entry, trace.MovementPoints[0]);
        Assert.All(trace.MovementPoints, point => Assert.True(row.RowBounds.Contains(point.X, point.Y)));
        Assert.Equal(((int)row.SafeHoverPoint.X, (int)row.SafeHoverPoint.Y), cursor.GetPosition());
    }

    [Fact]
    public void CursorLeavesRowBeforeCapture_DetectsCursorLoss()
    {
        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1485, 230, 425, 26),
            SafeHoverPoint = (1560, 243)
        };

        var driftedCursor = (1457, 190); // Outside row Y
        bool cursorInRow = row.RowBounds.Contains(driftedCursor.Item1, driftedCursor.Item2);

        Assert.False(cursorInRow);
    }

    [Fact]
    public void RehoverAfterCursorLoss_RestoresCursorToTargetRow()
    {
        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1485, 230, 425, 26),
            SafeHoverPoint = (1560, 243)
        };

        var cursor = new FakeCursorController((1457, 190));
        var activator = new SyntheticHoverActivator(cursor, _ => { });

        activator.Activate((1490, 150), (1560, 243));

        Assert.True(row.RowBounds.Contains(cursor.GetPosition().X, cursor.GetPosition().Y));
    }

    [Fact]
    public void PixelDifferenceAlone_DoesNotClaimAdmitVisible()
    {
        // Diffuse noise without button structure
        using var before = new Bitmap(200, 30);
        using var after = new Bitmap(200, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                before.SetPixel(x, y, Color.FromArgb(40, 40, 40));
                after.SetPixel(x, y, Color.FromArgb(40, 40, 40));
            }
        }
        // Add single stray pixel
        after.SetPixel(150, 15, Color.FromArgb(200, 200, 200));

        var row = new WaitingParticipantRowCandidate { RowBounds = new(1485, 230, 200, 30) };
        var presence = PanelRowVisualInspector.InspectPostHoverVisualAdmit(before, after, row.RowBounds, row);

        Assert.Equal(VisualAdmitPresence.No, presence);
    }

    [Fact]
    public void AutomaticHoverFrameComparedWithManualSuccessfulHover_ComputesStateMatch()
    {
        using var afterRow = new Bitmap(300, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                afterRow.SetPixel(x, y, x > 150 ? Color.FromArgb(30, 120, 240) : Color.FromArgb(50, 50, 50));
            }
        }

        var row = new WaitingParticipantRowCandidate { RowBounds = new(1485, 230, 300, 30) };
        double similarity = PanelRowVisualInspector.CompareWithManualHoverState(afterRow, row);

        Assert.InRange(similarity, 0.80, 0.99);
    }

    [Fact]
    public void SmallActionCrop_3xAnd4xScaledCoordinatesMapAccurately()
    {
        var absoluteActionCrop = new BoundingRectangleInfo(1700, 230, 150, 30);

        // 3x Scale test
        var word3x = new OcrWord("Admit", new(90, 15, 120, 30));
        var scaled3x = new OcrResult([new("Admit", word3x.Bounds, [word3x])], [word3x], new(0, 0, 450, 90));
        var abs3x = ScreenCropGeometry.MapScaledOcrToAbsolute(scaled3x, absoluteActionCrop, 3);
        Assert.Equal(new BoundingRectangleInfo(1730, 235, 40, 10), abs3x.Words[0].Bounds);

        // 4x Scale test
        var word4x = new OcrWord("Admit", new(120, 20, 160, 40));
        var scaled4x = new OcrResult([new("Admit", word4x.Bounds, [word4x])], [word4x], new(0, 0, 600, 120));
        var abs4x = ScreenCropGeometry.MapScaledOcrToAbsolute(scaled4x, absoluteActionCrop, 4);
        Assert.Equal(new BoundingRectangleInfo(1730, 235, 40, 10), abs4x.Words[0].Bounds);
    }

    [Fact]
    public void VisualFallback_OnlyInsideVerifiedTargetRow()
    {
        using var before = new Bitmap(300, 30);
        using var after = new Bitmap(300, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                before.SetPixel(x, y, Color.FromArgb(40, 40, 40));
                after.SetPixel(x, y, Color.FromArgb(40, 40, 40));
            }
        }
        // Paint Admit button on right side
        for (int y = 5; y < 25; y++)
        {
            for (int x = 180; x < 230; x++)
            {
                after.SetPixel(x, y, Color.FromArgb(14, 114, 237));
            }
        }

        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1500, 230, 300, 30),
            SafeHoverPoint = (1560, 245)
        };
        var panel = new ParticipantsPanelDetectionResult
        {
            IsPanelVisible = true,
            PanelBounds = new(1480, 100, 350, 600)
        };

        var match = PanelRowVisualInspector.LocateVisualAdmitFallback(
            before,
            after,
            row.RowBounds,
            row,
            panel);

        Assert.NotNull(match);
        Assert.Equal(0.95, match.Confidence);
        Assert.True(match.AbsoluteBounds.X >= row.RowBounds.X + row.RowBounds.Width * 0.50);
        Assert.True(match.AbsoluteBounds.X + match.AbsoluteBounds.Width <= row.RowBounds.X + row.RowBounds.Width);
    }

    [Fact]
    public void VisualFallback_RefusesAmbiguousControls()
    {
        using var before = new Bitmap(300, 30);
        using var after = new Bitmap(300, 30);
        // Entire row changed (e.g. video resize or full selection highlight without distinct button)
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                before.SetPixel(x, y, Color.FromArgb(40, 40, 40));
                after.SetPixel(x, y, Color.FromArgb(200, 200, 200));
            }
        }

        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1500, 230, 300, 30),
            SafeHoverPoint = (1560, 245)
        };
        var panel = new ParticipantsPanelDetectionResult
        {
            IsPanelVisible = true,
            PanelBounds = new(1480, 100, 350, 600)
        };

        var match = PanelRowVisualInspector.LocateVisualAdmitFallback(
            before,
            after,
            row.RowBounds,
            row,
            panel);

        Assert.Null(match); // Ambiguity correctly rejected
    }

    [Fact]
    public void VisualFallback_NeverActsInBrowserScreenshot()
    {
        var decision = NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.Unknown,
            "msedge",
            "Chrome_WidgetWin_1",
            "Edge Browser",
            hasZoomParentOwnerChain: false);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void ConfirmedVisualAdmit_SendsExactlyOneClick()
    {
        var mouse = new FakeMouseInput();
        var click = new SingleClickExecutor(mouse);

        bool clicked = click.TryClick(1650, 245);

        Assert.True(clicked);
        Assert.Equal(1, mouse.ClickCount);
        Assert.Equal((1650, 245), mouse.LastTarget);
    }

    [Fact]
    public void ParticipantDisappearance_VerifiesAdmissionTwice()
    {
        var row = new WaitingParticipantRowCandidate
        {
            ParticipantName = "mohab mohamed",
            RowBounds = new(1500, 230, 300, 30)
        };
        var initialPanel = new ParticipantsPanelDetectionResult
        {
            IsPanelVisible = true,
            DeclaredWaitingCount = 1,
            Rows = [row]
        };

        var verifier = new PanelAdmissionVerifier(row, initialPanel);

        // Frame 1: participant disappeared
        var emptyPanel = new ParticipantsPanelDetectionResult
        {
            IsPanelVisible = true,
            DeclaredWaitingCount = 0,
            Rows = []
        };
        var decision1 = verifier.Observe(emptyPanel);
        Assert.Equal(PanelAdmissionVerificationKind.Pending, decision1.Kind);

        // Frame 2: participant still gone
        var decision2 = verifier.Observe(emptyPanel);
        Assert.Equal(PanelAdmissionVerificationKind.Verified, decision2.Kind);
    }

    private sealed class FakeCursorController : ICursorController
    {
        public FakeCursorController((int X, int Y) initial) { Position = initial; }
        public (int X, int Y) Position { get; private set; }
        public (int X, int Y) GetPosition() => Position;
        public void MoveTo(int x, int y) => Position = (x, y);
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
