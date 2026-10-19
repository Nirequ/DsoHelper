using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DsoHelper;

public partial class App : Application
{
    private bool _hidePending;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            window.PropertyChanged += (_, args) =>
            {
                if (args.Property != Window.WindowStateProperty)
                {
                    return;
                }

                if (window.WindowState != WindowState.Minimized)
                {
                    _hidePending = false;
                    return;
                }

                _hidePending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_hidePending && window.WindowState == WindowState.Minimized)
                    {
                        window.Hide();
                    }

                    _hidePending = false;
                });
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnTrayOpenClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnTrayExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { } window)
        {
            return;
        }

        _hidePending = false;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
