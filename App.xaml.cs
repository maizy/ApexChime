using System;
using System.Drawing;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ApexChime;

public partial class App : Application
{
    private const string InstanceName = @"Local\dev.maizy.ApexChime";
    private Mutex? mutex;
    private bool ownsMutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private MainWindow? window;
    internal bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        showEvent = new(false, EventResetMode.AutoReset, InstanceName + ".Show");
        mutex = new(false, InstanceName);
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }

        if (!ownsMutex)
        {
            if (!Array.Exists(e.Args, arg => arg == "--background"))
                showEvent.Set();
            Shutdown();
            return;
        }

        var store = new StateStore();
        SavedState state;
        try
        {
            state = store.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show("ApexChime could not read its settings in %LOCALAPPDATA%\\ApexChime.",
                "ApexChime", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        window = new MainWindow(store, state);
        MainWindow = window;
        CreateTrayIcon();
        SystemEvents.TimeChanged += SystemTimeChanged;
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!Array.Exists(e.Args, arg => arg == "--background"))
            ShowWindow();
    }

    private void CreateTrayIcon()
    {
        using var iconStream = GetResourceStream(new Uri("/ApexChime;component/Assets/ApexChime.ico", UriKind.Relative)).Stream;
        trayIcon = new Icon(iconStream, 32, 32);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ApexChime", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            IsExiting = true;
            Shutdown();
        }));
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "ApexChime",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
    }

    private void ShowWindow()
    {
        if (window is null)
            return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void SystemTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            TimeZoneInfo.ClearCachedData();
            window?.RefreshSchedule();
        });

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        IsExiting = !e.Cancel;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.TimeChanged -= SystemTimeChanged;
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        if (ownsMutex)
            mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ApexChime";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
            key.SetValue(AppName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static bool IsAccessError(Exception error) =>
        error is UnauthorizedAccessException or IOException or SecurityException;
}
