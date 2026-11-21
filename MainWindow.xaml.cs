using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ApexChime;

public partial class MainWindow : Window
{
    private readonly StateStore store;
    private readonly Schedule schedule;
    private readonly ObservableCollection<ScheduleRow> rows = [];
    private readonly AlertPresenter presenter = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool ready;
    private bool savePending;

    internal MainWindow(StateStore store, SavedState state)
    {
        this.store = store;
        schedule = new(state);
        InitializeComponent();

        foreach (var minutes in new[] { 3, 5, 8, 10, 15, 20, 25, 30, 45, 60 })
        {
            var button = new Button { Content = $"{minutes} min", Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => StartTimer(Schedule.StartTimer(minutes, DateTimeOffset.UtcNow));
            PresetButtons.Children.Add(button);
        }

        ScheduleList.ItemsSource = rows;
        SoundSelector.ItemsSource = AlertAudio.Options;
        SoundSelector.SelectedItem = AlertAudio.Options.FirstOrDefault(option => option.Sound == state.Sound) ?? AlertAudio.Options[0];
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version!.ToString(3)}";
        NotificationsToggle.IsChecked = state.NotificationsEnabled;
        try { StartupToggle.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception error) when (StartupRegistration.IsAccessError(error))
        {
            SetStatus("ApexChime could not read the Windows autostart setting.");
        }

        ready = true;
        RefreshSchedule();
        ticker.Tick += (_, _) => RefreshSchedule();
        ticker.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DesktopWindow.UseDarkCaption(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ticker.Stop();
        presenter.Clear();
        SaveIfNeeded();
        base.OnClosed(e);
    }

    internal void RefreshSchedule()
    {
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        List<ScheduledItem> alerts = [];
        savePending |= schedule.Advance(now, zone, alerts);
        SaveIfNeeded();

        if (alerts.Count > 0 && schedule.State.NotificationsEnabled)
            presenter.Show(alerts.OrderBy(item => item.Due), schedule.State.Sound);

        var items = schedule.Items(now, zone).ToList();
        var wanted = items.Select(item => item.Id).ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(rows[index].Id))
                rows.RemoveAt(index);
        }

        var byId = rows.ToDictionary(row => row.Id);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!byId.TryGetValue(item.Id, out var row))
            {
                row = new ScheduleRow(item);
                rows.Insert(index, row);
            }
            else if (rows[index] != row)
            {
                rows.Move(rows.IndexOf(row), index);
            }
            row.Update(item.Due, now, zone);
        }

        EmptySchedule.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RunTimer(object sender, RoutedEventArgs e) => RunCustomTimer();

    private void TimerInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        RunCustomTimer();
    }

    private void RunCustomTimer()
    {
        var timer = Schedule.ParseTimer(TimerInput.Text, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        if (timer is null)
        {
            SetStatus("Enter minutes, H:MM, or !HH:MM for a timer up to 24 hours.");
            TimerInput.Focus();
            return;
        }
        TimerInput.Clear();
        StartTimer(timer);
    }

    private void StartTimer(Countdown timer)
    {
        schedule.State.Timers.Add(timer);
        CommitChange();
    }

    private void AddReminder(object sender, RoutedEventArgs e) => AddDailyReminder();

    private void ReminderInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddDailyReminder();
    }

    private void AddDailyReminder()
    {
        if (!Schedule.TryReadClock(ReminderTime.Text, out var hour, out var minute))
        {
            SetStatus("Enter a reminder time from 00:00 to 23:59.");
            ReminderTime.Focus();
            return;
        }

        var text = ReminderText.Text.Trim();
        if (text.Length > 30)
        {
            SetStatus("Enter a reminder message of up to 30 characters.");
            ReminderText.Focus();
            return;
        }

        var localNow = DateTime.Now;
        schedule.State.Reminders.Add(new(hour, minute, text)
        {
            LastFired = localNow.Hour == hour && localNow.Minute == minute ? DateOnly.FromDateTime(localNow) : null
        });
        ReminderTime.Clear();
        ReminderText.Clear();
        CommitChange();
    }

    private void RemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id })
            return;

        schedule.State.Timers.RemoveAll(timer => timer.Id == id);
        schedule.State.Reminders.RemoveAll(reminder => reminder.Id == id);
        presenter.Remove(id);
        CommitChange();
    }

    private void ToggleStartup(object sender, RoutedEventArgs e)
    {
        var enabled = StartupToggle.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            if (!savePending)
                SetStatus("");
        }
        catch (Exception error) when (StartupRegistration.IsAccessError(error))
        {
            StartupToggle.IsChecked = !enabled;
            SetStatus("ApexChime could not change the Windows autostart setting.");
        }
    }

    private void ToggleNotifications(object sender, RoutedEventArgs e)
    {
        schedule.State.NotificationsEnabled = NotificationsToggle.IsChecked == true;
        if (!schedule.State.NotificationsEnabled)
            presenter.Clear();
        CommitChange();
    }

    private void ChangeSound(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || SoundSelector.SelectedItem is not SoundOption option)
            return;

        schedule.State.Sound = option.Sound;
        CommitChange();
    }

    private void CommitChange()
    {
        SetStatus("");
        savePending = true;
        RefreshSchedule();
    }

    private void SaveIfNeeded()
    {
        const string saveError = "ApexChime could not save your settings. It will retry automatically.";
        if (!savePending)
            return;
        try
        {
            store.Save(schedule.State);
            savePending = false;
            if (Status.Text == saveError)
                SetStatus("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus(saveError);
        }
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        Status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

internal sealed class ScheduleRow(ScheduledItem item) : INotifyPropertyChanged
{
    public Guid Id { get; } = item.Id;
    public bool IsReminder { get; } = item.IsReminder;
    public string Title { get; } = item.Title;
    public string RemoveLabel => IsReminder ? $"Remove reminder: {Title}" : $"Remove timer: {Title}";
    public string Remaining { get; private set; } = "";
    public string Due { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DateTimeOffset due, DateTimeOffset now, TimeZoneInfo zone)
    {
        var seconds = (int)Math.Ceiling((due - now).TotalSeconds);
        var remaining = due <= now ? "Done" : FormattableString.Invariant($"{seconds / 60:00}:{seconds % 60:00}");
        if (Remaining != remaining)
        {
            Remaining = remaining;
            PropertyChanged?.Invoke(this, new(nameof(Remaining)));
        }

        var localTime = TimeZoneInfo.ConvertTime(due, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (Due != localTime)
        {
            Due = localTime;
            PropertyChanged?.Invoke(this, new(nameof(Due)));
        }
    }
}
