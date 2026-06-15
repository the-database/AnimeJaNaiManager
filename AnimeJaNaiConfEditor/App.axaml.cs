using AnimeJaNaiConfEditor.ViewModels;
using AnimeJaNaiConfEditor.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using ReactiveUI;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Dialog handlers are `async void`, so an exception thrown from one (e.g. a
            // FluentAvalonia dialog bug) is posted to the UI dispatcher and would otherwise
            // crash the whole app. Swallow it here so a single dialog failure degrades to a
            // no-op instead of taking down the Manager.
            Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = true;
            TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };

                // When another launch (e.g. a repeated Ctrl+E from mpv) signals this instance,
                // bring the existing window to the front instead of opening a new one.
                Program.ActivationRequested += () => BringToFront(desktop.MainWindow);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void BringToFront(Window? window)
        {
            if (window is null)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
        }
    }
}