using AnimeJaNaiConfEditor.Services;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace AnimeJaNaiConfEditor
{
    internal class Program
    {
        // Shared id for both the single-instance mutex and the activation pipe. Mutex and named
        // pipes are cross-platform in .NET (pipes are Unix domain sockets on Linux/macOS), so this
        // works without any OS-specific code.
        private const string SingleInstanceId = "AnimeJaNaiManager_SingleInstance";

        // Held in a static field for the process lifetime so the mutex stays owned until exit.
        private static Mutex? _singleInstanceMutex;

        // Raised on the UI thread when another launch asks this instance to come to the front,
        // with that launch's arguments (none from a Manager before --page).
        public static event Action<LaunchArgs>? ActivationRequested;

        // What this instance was started with (--page, --player-ipc, --owner-window).
        public static LaunchArgs StartupArgs { get; private set; } = LaunchArgs.None;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            StartupArgs = LaunchArgs.Parse(args);

            // Single-instance guard. mpv's Ctrl+E binding can fire this launch many times from one
            // held keypress; without this, each would open another editor window. Only the first
            // instance wins the mutex - any extra launch signals the running one to surface its
            // window, then exits. The OS releases the mutex on process exit, so no cleanup is needed.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceId, out bool createdNew);
            if (!createdNew)
            {
                SignalExistingInstance(StartupArgs);
                return;
            }

            StartActivationListener();
            try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            catch (Exception ex) { Services.Diagnostics.DiagnosticLog.Error(ex); throw; }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI(_ => { });

        // Duplicate launch: connect to the running instance's pipe to ask it to come forward, and
        // pass this launch's arguments on (the tab to open, the player to apply settings to).
        // Best effort - if the first instance isn't listening yet, we just exit quietly.
        // The message is the byte 1 and then the arguments as JSON; a Manager from before reads
        // only the byte and comes to the front as it always did.
        private static void SignalExistingInstance(LaunchArgs args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstanceId, PipeDirection.Out);
                client.Connect(1000);
                client.Write(args.ToActivationMessage());
                client.Flush();
            }
            catch
            {
                // No reachable instance to signal - nothing more to do.
            }
        }

        // First instance: listen for activation requests from later launches and forward them to
        // the UI thread, where App raises the main window.
        private static void StartActivationListener()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(SingleInstanceId, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        server.WaitForConnection();
                        _ = server.ReadByte();
                        var args = LaunchArgs.FromActivationPayload(ReadPayload(server));
                        Dispatcher.UIThread.Post(() => ActivationRequested?.Invoke(args));
                    }
                    catch
                    {
                        // Keep listening despite transient pipe errors.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceActivationListener",
            };
            thread.Start();
        }

        // The rest of an activation message, up to the sender closing its end. An older Manager
        // sends nothing after the first byte. Capped so a stray client can't hold the listener.
        private static byte[] ReadPayload(NamedPipeServerStream server)
        {
            using var payload = new MemoryStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buffer = new byte[4096];
            try
            {
                int read;
                while (payload.Length < 64 * 1024 &&
                       (read = server.ReadAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
                    payload.Write(buffer, 0, read);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // use what arrived
            }
            return payload.ToArray();
        }
    }
}
