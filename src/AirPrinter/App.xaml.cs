using System.Windows;
using AirPrinter.Services;
using MessageBox = System.Windows.MessageBox;

namespace AirPrinter;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Внутренняя ошибка (" + args.Exception.GetType().Name + ").", "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Length >= 3 && e.Args[0] == "--elevated-setup")
        {
            int setup = ElevatedSetup.Execute(e.Args[1], e.Args[2]);
            Shutdown(setup);
            return;
        }

        if (e.Args.Any(arg => arg == "--self-test"))
        {
            int code = SelfTest.Run(Console.Out);
            Shutdown(code);
            return;
        }

        if (Elevation.IsElevated())
        {
            if (Elevation.TryStartMedium(e.Args))
            {
                Shutdown(0);
                return;
            }

            MessageBox.Show("Откройте AirPrinter без прав администратора. Печать не запускается в процессе администратора.", "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        _instance = new Mutex(true, @"Local\AirPrinter.Bridge", out bool created);
        if (!created)
        {
            _instance.Dispose();
            _instance = null;
            MessageBox.Show("AirPrinter уже запущен.", "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Theme.Apply(this);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
