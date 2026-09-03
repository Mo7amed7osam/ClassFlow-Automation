using System.Globalization;
using System.Windows.Data;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>Shows the weekday a schedule actually runs on: the date's own day for exact dates, the ticked days otherwise.</summary>
public sealed class ScheduleDaysConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MeetingSchedule schedule) return string.Empty;
        if (schedule.OccurrenceDate is { } date) return $"{date.DayOfWeek} (once)";
        if (schedule.Days == ScheduleDays.None) return "—";
        if (schedule.Days == ScheduleDays.EveryDay) return "Every day";
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Where(day => schedule.Days.Includes(day))
            .Select(day => day.ToString()[..3]);
        return string.Join(", ", days);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
