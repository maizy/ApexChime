using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ApexChime;

internal enum AlertSound { None, Default, Information, Exclamation, Question, CriticalStop }

internal sealed record SoundOption(AlertSound Sound, string Label, SystemSound? Player);

internal static class AlertAudio
{
    public static IReadOnlyList<SoundOption> Options { get; } =
    [
        new(AlertSound.Default, "Windows default", SystemSounds.Beep),
        new(AlertSound.Information, "Information", SystemSounds.Asterisk),
        new(AlertSound.Exclamation, "Exclamation", SystemSounds.Exclamation),
        new(AlertSound.Question, "Question", SystemSounds.Question),
        new(AlertSound.CriticalStop, "Critical stop", SystemSounds.Hand),
        new(AlertSound.None, "No sound", null)
    ];

    public static void Play(AlertSound sound) =>
        (Options.FirstOrDefault(option => option.Sound == sound) ?? Options[0]).Player?.Play();
}

internal sealed class AlertPresenter
{
    private readonly List<(ScheduledItem Item, long Expires)> active = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AlertWindow? window;

    public AlertPresenter() => timer.Tick += (_, _) => Update();

    public void Show(IEnumerable<ScheduledItem> items, AlertSound sound)
    {
        var expires = Environment.TickCount64 + 15_000;
        active.AddRange(items.Select(item => (item, expires)));
        Update();
        AlertAudio.Play(sound);
    }

    public void Remove(Guid id)
    {
        active.RemoveAll(alert => alert.Item.Id == id);
        Update();
    }

    public void Clear()
    {
        active.Clear();
        Update();
    }

    private void Update()
    {
        var now = Environment.TickCount64;
        active.RemoveAll(alert => alert.Expires <= now);
        if (active.Count == 0)
        {
            timer.Stop();
            window?.Close();
            window = null;
            return;
        }

        var first = active[0].Item;
        var wasHidden = window is null;
        window ??= new AlertWindow();
        window.SetContent(first, active.Count - 1);
        if (wasHidden)
            window.Show();
        if (!timer.IsEnabled)
            timer.Start();
    }
}

public partial class AlertWindow : Window
{
    private readonly Forms.Screen screen = Forms.Screen.FromHandle(DesktopWindow.GetForegroundWindow());

    internal AlertWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Position();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Position, DispatcherPriority.Loaded);
    }

    internal void SetContent(ScheduledItem item, int otherCount)
    {
        Heading.Text = item.IsReminder ? "Daily reminder" : "Timer finished";
        Message.Text = item.Title;
        More.Text = otherCount == 1 ? "+1 more alert" : $"+{otherCount} more alerts";
        More.Visibility = otherCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        DesktopWindow.MakeClickThrough(handle);
        var area = screen.WorkingArea;
        DesktopWindow.Move(handle, area.Left, area.Top);
    }

    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var dpi = DesktopWindow.GetDpiForWindow(handle) / 96.0;
        var area = screen.WorkingArea;
        var left = area.Right - (int)Math.Ceiling((ActualWidth + 18) * dpi);
        var top = area.Bottom - (int)Math.Ceiling((ActualHeight + 18) * dpi);
        DesktopWindow.Move(handle, Math.Max(area.Left, left), Math.Max(area.Top, top));
    }
}

internal static class DesktopWindow
{
    public static void UseDarkCaption(IntPtr handle)
    {
        var enabled = 1;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    public static void MakeClickThrough(IntPtr handle)
    {
        const int extendedStyle = -20;
        const int transparent = 0x20;
        const int noActivate = 0x08000000;
        SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | transparent | noActivate);
    }

    public static void Move(IntPtr handle, int left, int top)
    {
        const uint noSizeZOrderOrActivation = 0x0015;
        SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, noSizeZOrderOrActivation);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr handle);
}
