using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ApexChime;

internal sealed class SavedState
{
    public bool NotificationsEnabled { get; set; } = true;
    public AlertSound Sound { get; set; } = AlertSound.Default;
    public List<Countdown> Timers { get; set; } = [];
    public List<DailyReminder> Reminders { get; set; } = [new(22, 0, "go to sleep")];
}

internal sealed record Countdown(DateTimeOffset Due, string Title)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Notified { get; set; }
}

internal sealed record DailyReminder(int Hour, int Minute, string Text)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateOnly? LastFired { get; set; }
}

internal sealed record ScheduledItem(Guid Id, bool IsReminder, string Title, DateTimeOffset Due);

internal sealed class Schedule(SavedState state)
{
    public static readonly TimeSpan FinishedVisibility = TimeSpan.FromSeconds(30);
    public SavedState State { get; } = state;

    public static Countdown? ParseTimer(string input, DateTimeOffset now, TimeZoneInfo zone)
    {
        input = input.Trim();
        if (input.StartsWith('!'))
        {
            if (!TryReadClock(input[1..], out var hour, out var minute))
                return null;

            var date = LocalDate(now, zone);
            var clock = new TimeOnly(hour, minute);
            foreach (var target in new[] { date.ToDateTime(clock), date.AddDays(1).ToDateTime(clock) })
            {
                if (zone.IsInvalidTime(target))
                    continue;

                var due = ClockInstants(target, zone).FirstOrDefault(candidate => candidate > now);
                if (due != default && due - now <= TimeSpan.FromHours(24))
                    return new(due, $"Until {hour:00}:{minute:00}");
            }
            return null;
        }

        int totalMinutes;
        if (input.Contains(':'))
        {
            var parts = input.Split(':');
            if (parts.Length != 2 || !ReadNumber(parts[0], out var hours) || hours > 24 ||
                parts[1].Length != 2 || !ReadNumber(parts[1], out var minutes) || minutes > 59)
                return null;

            totalMinutes = hours * 60 + minutes;
        }
        else if (!ReadNumber(input, out totalMinutes))
        {
            return null;
        }

        return totalMinutes is > 0 and <= 1440 ? StartTimer(totalMinutes, now) : null;
    }

    public static Countdown StartTimer(int minutes, DateTimeOffset now) =>
        new(now.ToUniversalTime().AddMinutes(minutes), $"{minutes} min timer");

    public static bool TryReadClock(string input, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = input.Trim().Split(':');
        return parts.Length == 2 && parts[0].Length is 1 or 2 && parts[1].Length == 2 &&
            ReadNumber(parts[0], out hour) && hour < 24 &&
            ReadNumber(parts[1], out minute) && minute < 60;
    }

    public bool Advance(DateTimeOffset now, TimeZoneInfo zone, List<ScheduledItem> alerts)
    {
        var changed = State.Timers.RemoveAll(timer => now - timer.Due >= FinishedVisibility) > 0;
        foreach (var timer in State.Timers.Where(timer => !timer.Notified && timer.Due <= now))
        {
            timer.Notified = true;
            alerts.Add(new(timer.Id, false, timer.Title, timer.Due));
            changed = true;
        }

        var today = LocalDate(now, zone);
        foreach (var reminder in State.Reminders)
        {
            // The final minute of the previous day can still be within the delivery window.
            foreach (var date in new[] { today, today.AddDays(-1) })
            {
                if (reminder.LastFired == date)
                    continue;

                var due = Occurrence(reminder, date, zone);
                var elapsed = now - due;
                if (elapsed < TimeSpan.Zero || elapsed >= FinishedVisibility)
                    continue;

                reminder.LastFired = date;
                alerts.Add(new(reminder.Id, true, ReminderTitle(reminder), due));
                changed = true;
                break;
            }
        }

        return changed;
    }

    public IEnumerable<ScheduledItem> Items(DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = LocalDate(now, zone);
        var timers = State.Timers.Select(timer => new ScheduledItem(timer.Id, false, timer.Title, timer.Due));
        var reminders = State.Reminders.Select(reminder =>
        {
            var due = Occurrence(reminder, date, zone);
            if (due <= now || reminder.LastFired == date)
                due = Occurrence(reminder, date.AddDays(1), zone);

            return new ScheduledItem(reminder.Id, true, ReminderTitle(reminder), due);
        });
        return timers.Concat(reminders).OrderBy(item => item.Due);
    }

    private static bool ReadNumber(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static string ReminderTitle(DailyReminder reminder) =>
        string.IsNullOrEmpty(reminder.Text) ? "Daily reminder" : reminder.Text;

    private static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    private static DateTimeOffset Occurrence(DailyReminder reminder, DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(reminder.Hour, reminder.Minute));
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        return ClockInstants(local, zone).First();
    }

    private static IEnumerable<DateTimeOffset> ClockInstants(DateTime local, TimeZoneInfo zone)
    {
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        return offsets.OrderDescending().Select(offset => new DateTimeOffset(local, offset));
    }
}
