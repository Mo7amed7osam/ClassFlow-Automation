using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using ZoomAutoAdmit.WindowsUI.Views;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class RedesignViewTests
{
    private sealed class PreviewAttendance : IAttendanceHistoryReader
    {
        public Task<AttendanceHistory> ReadAsync(CancellationToken token = default) => Task.FromResult(new AttendanceHistory(
            [new("preview-only", new(Guid.Parse("11111111-1111-1111-1111-111111111111"), DateTimeOffset.Now,
                ZoomAutoAdmit.Attendance.AttendanceSource.Web, [new("Preview student")],
                ZoomAutoAdmit.Attendance.SnapshotTrigger.Manual, false, null)
                { Meeting = new("preview", "Preview account", "https://zoom.us/j/12345678901", DateTimeOffset.Now, "Web") })], 0, false));
    }
    // This fake is deliberately incapable of launching Zoom, starting a scheduler or invoking AI.
    private sealed class PreviewService : IWindowsUiService
    {
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("UI preview", "Ready", "", false, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([new("preview", "Preview account", "") { ZoomEmail = "preview@example.com" }]);
        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionDisplayInfo>>([]);
        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingSchedule>>([]);
        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionDisplayInfo> StartMeetingAsync(string accountId, string meetingUrl, EnginePreference preference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void NavigationRetainsExistingCommandIndexesAndSupportsSearch()
    {
        using var model = new MainViewModel(new PreviewService());
        model.ShowStartMeetingCommand.Execute(null);
        Assert.Equal(1, model.SelectedTabIndex);
        model.NavigateCommand.Execute("2");
        Assert.Equal("Accounts", model.PageTitle);
        Assert.Equal(2, model.SelectedNavigation!.Index);
        model.NavigationSearch = "groups";
        Assert.Equal(6, Assert.Single(model.FilteredNavigation).Index);
        model.SelectedNavigation = model.FilteredNavigation[0];
        Assert.Equal(6, model.SelectedTabIndex);
        model.NavigationSearch = "";
        Assert.Equal(13, model.FilteredNavigation.Count);
        model.NavigateCommand.Execute("99");
        Assert.Equal(6, model.SelectedTabIndex);
    }

    [Fact]
    public async Task EveryPageRendersInBothThemesWithoutBrokenBindingsOrRuntimeActions()
    {
        var credentials = new AiSetupTests.MemoryStore();
        var ai = new AiSetupTests.FakeAi();
        using var model = new MainViewModel(new PreviewService(), aiCredentials: credentials, aiService: ai, attendanceHistory: new PreviewAttendance());
        await model.Attendance.RefreshAsync();
        await model.Accounts.RefreshAsync();
        model.Accounts.SelectedAccount = model.Accounts.Items[0];
        await model.StartMeeting.RefreshAccountsAsync();
        await model.Dashboard.RefreshAsync();
        await model.Schedules.RefreshAsync();
        var template = Environment.GetEnvironmentVariable("ZOOM_SCHEDULE_TEMPLATE");
        if (!string.IsNullOrWhiteSpace(template)) await model.Schedules.PreviewImportAsync(template);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var group = new RosterGroup("preview", "Preview group", DateTimeOffset.UtcNow,
                    [new("preview-student", "preview", 1, "Preview student", [])]);
                model.Roster.Groups.Add(group);
                model.Roster.SelectedGroup = group;
                model.AiMatching.SelectedGroup = group;
                model.Roster.SelectedStudent = group.Students[0];
                window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                Pump();
                var tabs = Descendants(window).OfType<TabControl>().Single();
                Assert.Equal(14, tabs.Items.Count);
                foreach (bool dark in new[] { true, false })
                {
                    window.ApplyTheme(dark);
                    Assert.Equal(dark ? "#FF05080F" : "#FFE6EFFB", ((SolidColorBrush)window.FindResource("CanvasBrush")).Color.ToString());
                    for (int page = 0; page < tabs.Items.Count; page++)
                    {
                        model.SelectedTabIndex = page;
                        Pump();
                        window.UpdateLayout();
                        Assert.Equal(page, tabs.SelectedIndex);
                        Assert.NotNull(tabs.SelectedContent);
                        if (page == 1)
                        {
                            var accountPicker = Descendants(window).OfType<ComboBox>().First();
                            var visibleText = Descendants(accountPicker).OfType<TextBlock>().Select(t => t.Text).ToArray();
                            Assert.Contains("Preview account", visibleText);
                            Assert.DoesNotContain(visibleText, t => t.Contains(nameof(WindowsMeetingAccountMetadata)));
                        }
                        var buttons = Descendants(window).OfType<Button>().ToArray();
                        if (page == 2)
                        {
                            Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Accounts.SwitchAccountCommand));
                            Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Accounts.SaveCommand));
                        }
                        if (page == 3) Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Schedules.SaveCommand));
                        if (page == 3) Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Schedules.UploadCommand));
                        if (page == 9)
                        {
                            Assert.Contains(buttons, b => b.Content as string == "Test connection");
                            Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.AiMatching.MatchCommand));
                            Assert.Single(Descendants(window).OfType<PasswordBox>());
                            var modelPicker = Descendants(window).OfType<ComboBox>().Single(c => c.IsEditable);
                            var modelEditor = (TextBox)modelPicker.Template.FindName("PART_EditableTextBox", modelPicker);
                            Assert.True(modelEditor.IsVisible);
                            Assert.Equal(model.AiMatching.Model, modelEditor.Text);
                            modelEditor.Text = "custom-model-id";
                            Pump();
                            Assert.Equal("custom-model-id", model.AiMatching.Model);
                            modelPicker.SelectedItem = "gpt-5.4-mini";
                            Pump();
                            Assert.Equal("gpt-5.4-mini", model.AiMatching.Model);
                        }
                        if (page == 8)
                        {
                            Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Attendance.MatchCommand));
                            // The page is about a session that was run, so it offers sessions - not
                            // one row per reading, and no table of raw observed names.
                            Assert.Contains(Descendants(window).OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, model.Attendance.Sessions));
                            Assert.DoesNotContain(Descendants(window).OfType<DataGrid>(), g => ReferenceEquals(g.ItemsSource, model.Attendance.Participants));
                        }
                        if (page == 12)
                        {
                            Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.AiMatching.TestSavedCommand));
                            var unavailable = buttons.Where(b => b.Content as string is "View usage" or "Manage weights").ToArray();
                            Assert.Equal(2, unavailable.Length);
                            Assert.All(unavailable, b => Assert.False(b.IsEnabled));
                        }
                        if (page == 4) Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Logs.ClearCommand));
                        if (page == 6) Assert.Contains(buttons, b => ReferenceEquals(b.Command, model.Roster.MoveUpCommand));
                        Assert.False(BindingOperations.GetBindingExpression(tabs, Selector.SelectedIndexProperty)!.HasError);
                        foreach (var element in Descendants(window))
                        {
                            foreach (var property in new[] { TextBox.TextProperty, TextBlock.TextProperty,
                                         ItemsControl.ItemsSourceProperty, Selector.SelectedItemProperty, Button.CommandProperty })
                            {
                                var expression = BindingOperations.GetBindingExpressionBase(element, property);
                                Assert.False(expression?.HasError ?? false, $"Broken binding: {element.GetType().Name}.{property.Name}, page {page}");
                            }
                        }
                        SavePreview(window, $"{(dark ? "dark" : "light")}-{page:00}.png");
                    }
                }
                model.SelectedTabIndex = 1;
                Pump();
                var combo = Descendants(window).OfType<ComboBox>().First();
                combo.IsDropDownOpen = true;
                Pump();
                Assert.True(combo.IsDropDownOpen);
                combo.IsDropDownOpen = false;
                // Verify the primary launch button still calls the original command.
                Assert.Contains(Descendants(window).OfType<Button>(), b => ReferenceEquals(b.Command, model.StartMeeting.StartCommand));
                window.Width = 1100;
                window.Height = 720;
                model.SelectedTabIndex = 0;
                Pump();
                SavePreview(window, "light-minimum.png");
                var setupButton = Descendants(window).OfType<Button>().Single(b => b.Content as string == "AI setup →");
                var setupLabel = Descendants(setupButton).OfType<TextBlock>().Single();
                Assert.True(setupLabel.ActualWidth >= setupLabel.DesiredSize.Width - .5, "AI setup label must fit at minimum window width.");
                // Each page now states its own purpose, so find the subtitle by what the model says
                // it is rather than by one sentence that used to be shared by every page.
                var subtitle = Descendants(window).OfType<TextBlock>().Single(t => t.Text == model.PageSubtitle);
                // DesiredSize includes Margin; ActualHeight only describes the text's layout box.
                Assert.True(subtitle.ActualHeight + subtitle.Margin.Top + subtitle.Margin.Bottom >= subtitle.DesiredSize.Height - .5);
                var subtitleBottom = subtitle.TranslatePoint(new Point(0, subtitle.ActualHeight), window).Y;
                var contentTop = tabs.TranslatePoint(new Point(0, 0), window).Y;
                Assert.True(subtitleBottom <= contentTop, "Page subtitle must fit above the page content.");
                var ghost = Descendants(window).OfType<GhostCursorOverlay>().Single();
                Assert.False(ghost.IsHitTestVisible);
                Assert.False(ghost.Focusable);
                ghost.IsEffectEnabled = true;
                ghost.TrackPointer(new Point(500, 300));
                Assert.True(ghost.IsAnimating);
                for (int i = 0; i < 8; i++)
                {
                    ghost.TrackPointer(new Point(600 + i * 14, 300 + Math.Sin(i * .5) * 30));
                    PumpFor(TimeSpan.FromMilliseconds(40));
                }
                SavePreview(window, "ghost-cursor.png");
                Assert.True(ghost.ActualWidth > 500);
                Assert.True(ghost.ActualHeight > 300);
                Assert.True(ghost.HasVisibleTrail);
                var withTrail = CapturePixels(window);
                ghost.IsEffectEnabled = false;
                Pump();
                Assert.False(withTrail.AsSpan().SequenceEqual(CapturePixels(window)), "Ghost trail must visibly alter rendered pixels, not only start a timer.");
                Assert.False(ghost.IsAnimating);
                Assert.IsNotType<GhostCursorOverlay>(window.InputHitTest(new Point(500, 300)));
                // Exercise the real PasswordBox -> Click handler -> ViewModel -> service path with a fake remote service.
                model.SelectedTabIndex = 9;
                Pump();
                var password = Descendants(window).OfType<PasswordBox>().Single();
                password.Password = "synthetic-ui-key";
                Descendants(window).OfType<Button>().Single(b => b.Content as string == "Test & save securely")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Assert.Equal("", password.Password);
                Assert.True(model.AiMatching.IsReady);
                Assert.Equal(1, ai.Tests);
                Assert.Equal("synthetic-ui-key", credentials.Value!.ApiKey);
                Assert.Contains("Done", model.AiMatching.Status);
                // The alternative test button must use the populated PasswordBox, not ignore it.
                password.Password = "replacement-ui-key";
                ai.Failure = new ZoomAutoAdmit.AttendanceMatching.AiProviderException(System.Net.HttpStatusCode.Unauthorized, "invalid_api_key");
                Descendants(window).OfType<Button>().Single(b => b.Content as string == "Test connection")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Assert.Equal(2, ai.Tests);
                Assert.False(model.AiMatching.IsReady);
                Assert.Contains("Not valid", model.AiMatching.Status);
                Assert.Equal("synthetic-ui-key", credentials.Value!.ApiKey);
                SavePreview(window, "ai-invalid-key.png");
                Assert.Equal("replacement-ui-key", ai.LastKey);
                ai.Failure = null;
                model.SelectedTabIndex = 8;
                var savedTest = model.AiMatching.TestAndSaveAsync(null);
                Assert.True(savedTest.IsCompletedSuccessfully); // This test adapter completes synchronously.
                model.AiMatching.AllowExternalMatching = true;
                Pump();
                Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, model.Attendance.MatchCommand)).Command.Execute(null);
                Pump();
                Assert.Single(model.Attendance.Results);
                SavePreview(window, "attendance-connected.png");
                // Exercise provider switching through the actual WPF picker, not only the ViewModel.
                window.Width = 1440; window.Height = 900;
                model.SelectedTabIndex = 9;
                Pump();
                password = Descendants(window).OfType<PasswordBox>().Single();
                password.Password = "old-provider-input";
                var providerPicker = Descendants(window).OfType<ComboBox>()
                    .Single(c => AutomationProperties.GetName(c) == "AI provider");
                providerPicker.SelectedItem = AiProvider.OpenRouter;
                Pump();
                Assert.Equal(AiProvider.OpenRouter, model.AiMatching.Provider);
                Assert.Equal("openai/gpt-4.1-mini", model.AiMatching.Model);
                Assert.Empty(password.Password);
                Assert.False(model.AiMatching.IsReady);
                Assert.False(model.AiMatching.AllowExternalMatching);
                password.Password = "synthetic-openrouter-ui-key";
                Descendants(window).OfType<Button>().Single(b => b.Content as string == "Test & save securely")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Assert.True(model.AiMatching.IsReady);
                Assert.Equal(AiProvider.OpenRouter, credentials.Value!.Provider);
                Assert.Contains("OpenRouter", model.AiMatching.Status);
                SavePreview(window, "openrouter-setup.png");
                completed.TrySetResult();
            }
            catch (Exception ex) { completed.TrySetException(ex); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void SavePreview(Window window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("ZOOM_UI_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, filename));
        encoder.Save(file);
    }

    private static byte[] CapturePixels(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
