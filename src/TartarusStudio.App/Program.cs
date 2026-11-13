using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace TartarusStudio.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var single = new Mutex(true, "TartarusStudio.SingleInstance", out bool first);
        if (!first) return 0; // already running (probably in the tray)
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

public sealed class App : Application
{
    private Controller? _controller;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _controller = new Controller();
            _window = new MainWindow(_controller);
            desktop.MainWindow = _window;
            desktop.Exit += (_, _) => { _controller.Save(); _controller.Dispose(); };

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TartarusStudio/Assets/icon.png")));
            var open = new NativeMenuItem("Open Tartarus Studio");
            open.Click += (_, _) => ShowWindow();
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => desktop.Shutdown();
            var tray = new TrayIcon { Icon = icon, ToolTipText = "Tartarus Studio", Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } } };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { tray });

            _controller.RestartBackend();
            bool minimized = (desktop.Args ?? Array.Empty<string>()).Contains("--minimized") || _controller.Config.Settings.StartMinimized;
            if (!minimized) _window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
