using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ApexChime;

internal sealed class SavedState
{
    public bool NotificationsEnabled { get; set; } = true;
    public AlertSound Sound { get; set; } = AlertSound.Default;
    public SavedWindowPlacement? WindowPlacement { get; set; }
    public List<Countdown> Timers { get; set; } = [];
    public List<DailyReminder> Reminders { get; set; } = [new(22, 0, "go to sleep")];
}

internal sealed record SavedWindowPlacement(int Left, int Top, double Width, double Height, bool Maximized);

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
        var (time, description) = SplitTimerInput(input);
        if (time.StartsWith('!'))
        {
            if (!TryReadClock(time[1..], out var hour, out var minute))
                return null;

            var date = LocalDate(now, zone);
            var clock = new TimeOnly(hour, minute);
            foreach (var target in new[] { date.ToDateTime(clock), date.AddDays(1).ToDateTime(clock) })
            {
                if (zone.IsInvalidTime(target))
                    continue;

                var due = ClockInstants(target, zone).FirstOrDefault(candidate => candidate > now);
                if (due != default && due - now <= TimeSpan.FromHours(24))
                    return new(due, description ?? DefaultTimerTitle((int)Math.Ceiling((due - now).TotalMinutes)));
            }
            return null;
        }

        int totalMinutes;
        if (TryReadNumber(time, out var minutes))
        {
            totalMinutes = minutes;
        }
        else if (TryReadHoursAndMinutes(time, maxHours: 24, out var hours, out minutes))
        {
            totalMinutes = hours * 60 + minutes;
        }
        else
        {
            return null;
        }

        return totalMinutes is > 0 and <= 1440 ? StartTimer(totalMinutes, now, description) : null;
    }

    public static Countdown StartTimer(int minutes, DateTimeOffset now, string? description = null) =>
        new(now.ToUniversalTime().AddMinutes(minutes), description ?? DefaultTimerTitle(minutes));

    public static bool TryReadClock(string input, out int hour, out int minute)
    {
        hour = minute = 0;
        var clock = input.AsSpan().Trim();
        return clock.Length is >= 4 and <= 6 &&
            TryReadHoursAndMinutes(clock, maxHours: 23, out hour, out minute, maxHourDigits: 2);
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

    private static (string Time, string? Description) SplitTimerInput(string input)
    {
        var parts = input.Split((char[]?)null, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (parts.FirstOrDefault() ?? "", null);

        var time = parts[0];
        var hour = time.StartsWith('!') ? time.AsSpan(1) : time.AsSpan();
        if (TryReadNumber(hour, out var value) && value <= 24)
        {
            var tail = parts[1].Split((char[]?)null, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // A space followed by a valid two-digit minute can also be the time separator.
            if (tail[0].Length == 2 && TryReadNumber(tail[0], out var minute) && minute < 60)
                return ($"{time}:{tail[0]}", tail.Length == 2 ? tail[1] : null);
        }

        return (time, parts[1]);
    }

    private static bool TryReadHoursAndMinutes(ReadOnlySpan<char> input, int maxHours, out int hour, out int minute,
        int maxHourDigits = int.MaxValue)
    {
        hour = minute = 0;
        if (input.Length < 4 || !TryReadNumber(input[^2..], out minute) || minute >= 60)
            return false;

        var prefix = input[..^2];
        if (Rune.DecodeLastFromUtf16(prefix, out var separator, out var length) != OperationStatus.Done || Rune.IsDigit(separator))
            return false;

        var hours = prefix[..^length];
        return hours.Length <= maxHourDigits && TryReadNumber(hours, out hour) && hour <= maxHours;
    }

    private static bool TryReadNumber(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static string DefaultTimerTitle(int minutes) => $"{minutes} min timer";

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
