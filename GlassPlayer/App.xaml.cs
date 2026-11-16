using GlassPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassPlayer;

public partial class App : Application
{
    private const string MutexName = "GlassPlayer.SingleInstance";
    private const string ShowEventName = "GlassPlayer.ShowWindow";

    private Window? _window;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: if Glass Player is already running, bring it forward and quit.
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { }
            Environment.Exit(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                dispatcher.TryEnqueue(() => MainWindow.Current?.ShowFromTray());
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        var window = new MainWindow();
        _window = window;
        if (StartupService.LaunchedMinimized() && PlayerService.Instance.Settings.StartMinimized)
            window.StartHidden();
        else
            window.Activate();
    }
}
