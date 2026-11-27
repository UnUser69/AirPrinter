using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MessageBox = System.Windows.MessageBox;
using AirPrinter.Services;
using Forms = System.Windows.Forms;

namespace AirPrinter;

public partial class MainWindow : Window
{
    private readonly AirPrintBridge _bridge = new();
    private readonly ObservableCollection<string> _log = [];
    private readonly Forms.NotifyIcon _tray = new();
    private UserSettings _settings = new();
    private Icon? _icon;
    private bool _exit;
    private bool _suppress;
    private bool _shareEdited;
    private bool _balloonShown;
    private bool _powerFault;
    private string? _knownPrinter;
    private readonly SolidColorBrush _powerBrush = new(System.Windows.Media.Color.FromRgb(255, 255, 255));

    public MainWindow()
    {
        InitializeComponent();
        PowerButton.Background = _powerBrush;
        PowerButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1F));
        PowerButton.Content = "Включить";
        LogList.ItemsSource = _log;
        _log.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
        _bridge.Logged += AppendLog;
        _icon = LoadTrayIcon();
        _tray.Icon = _icon;
        _tray.Text = "AirPrinter";
        _tray.Visible = true;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Dispatcher.Invoke(ExitApplication);
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exit)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(2500, "AirPrinter", "Окно скрыто. Мост, если он включён, продолжает работать.", Forms.ToolTipIcon.Info);
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _bridge.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        base.OnClosed(e);
    }

    private async Task LoadAsync()
    {
        _settings = UserSettingsStore.Load();
        ShareBox.IsChecked = _settings.Share;
        if (!string.IsNullOrWhiteSpace(_settings.ShareName))
        {
            ShareNameBox.Text = _settings.ShareName;
            _shareEdited = true;
        }

        RefreshBonjour();
        await ReloadPrintersAsync();
        AppendLog("Выберите локальный принтер и включите AirPrint.");
        UpdateButtons();
    }

    private void RefreshBonjour()
    {
        try
        {
            BonjourStatus status = BonjourAdvertiser.GetStatus();
            if (status.ServiceInstalled && status.ServiceStatus != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                try
                {
                    BonjourAdvertiser.TryStartService();
                    status = BonjourAdvertiser.GetStatus();
                }
                catch (Exception ex)
                {
                    AppendLog("Не удалось запустить службу Bonjour: " + ex.GetType().Name);
                }
            }

            BonjourStatusText.Text = status.Message;
            bool builtin = !status.DllInstalled && !status.ServiceInstalled;
            BonjourStatusText.Foreground = status.IsReady || builtin
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 120, 70))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 70, 30));
        }
        catch (Exception ex)
        {
            BonjourStatusText.Text = ex.Message;
        }
    }

    private async Task ReloadPrintersAsync()
    {
        string? selected = (PrinterCombo.SelectedItem as InstalledPrinter)?.Name ?? _knownPrinter ?? _settings.PrinterName;
        List<InstalledPrinter> printers;
        try
        {
            printers = (await Task.Run(PrinterShareService.List)).ToList();
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось прочитать принтеры: " + ex.GetType().Name);
            return;
        }

        _suppress = true;
        PrinterCombo.ItemsSource = printers;
        InstalledPrinter? match = printers.FirstOrDefault(item => string.Equals(item.Name, selected, StringComparison.OrdinalIgnoreCase));
        PrinterCombo.SelectedItem = match ?? (printers.Count > 0 ? printers[0] : null);
        _suppress = false;
        ShowPrinter(PrinterCombo.SelectedItem as InstalledPrinter);
    }

    private void ShowPrinter(InstalledPrinter? printer)
    {
        if (printer == null)
        {
            PrinterDetailsText.Text = "Локальные принтеры не найдены. Подключите принтер по USB и установите его драйвер в Windows.";
            return;
        }

        _knownPrinter = printer.Name;
        string shared = printer.IsShared
            ? "включён (" + (printer.ShareName.Length == 0 ? "без имени" : printer.ShareName) + ")"
            : "выключен";
        PrinterDetailsText.Text =
            $"Статус: {printer.StatusText}. Порт: {printer.PortName}. Драйвер: {printer.DriverName}. {(printer.IsVirtual ? "Виртуальная очередь. " : "")}Цвет: {(printer.SupportsColor ? "да" : "нет")}, двусторонняя: {(printer.CanDuplex ? "да" : "нет")}. Общий доступ Windows: {shared}.";
        if (!_shareEdited)
        {
            bool previous = _suppress;
            _suppress = true;
            ShareNameBox.Text = printer.IsShared && printer.ShareName.Length > 0
                ? printer.ShareName
                : PrinterShareService.MakeShareName(printer.Name);
            _suppress = previous;
        }
    }

    private async void RecheckBonjour_Click(object sender, RoutedEventArgs e)
    {
        RefreshBonjour();
        await Task.CompletedTask;
    }

    private void DownloadBonjour_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BonjourAdvertiser.OpenDownloadPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshPrinters_Click(object sender, RoutedEventArgs e)
    {
        await ReloadPrintersAsync();
    }

    private async void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        _shareEdited = false;
        InstalledPrinter? printer = PrinterCombo.SelectedItem as InstalledPrinter;
        ShowPrinter(printer);
        if (_bridge.IsRunning && printer != null)
        {
            await ApplyPrinterChangeAsync(printer);
        }
    }

    private async void ShareBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || !IsLoaded || !_bridge.IsRunning)
        {
            return;
        }

        InstalledPrinter? printer = PrinterCombo.SelectedItem as InstalledPrinter;
        if (printer != null)
        {
            await ApplyPrinterChangeAsync(printer);
        }
    }

    private void ShareNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppress && IsLoaded)
        {
            _shareEdited = true;
        }
    }

    private async void TestPage_Click(object sender, RoutedEventArgs e)
    {
        InstalledPrinter? printer = PrinterCombo.SelectedItem as InstalledPrinter;
        if (printer == null)
        {
            MessageBox.Show("Выберите принтер.", "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await Task.Run(() => PrinterShareService.PrintTestPage(printer.Name));
            AppendLog("Пробная страница отправлена в «" + printer.Name + "».");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartBridgeAsync()
    {
        InstalledPrinter? printer = PrinterCombo.SelectedItem as InstalledPrinter;
        if (printer == null)
        {
            _powerFault = true;
            UpdateButtons();
            MessageBox.Show("Выберите принтер в боковом разделе «Принтер».", "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool share = ShareBox.IsChecked == true;
        string shareName = ShareNameBox.Text.Trim();
        if (share)
        {
            string? error = PrinterShareService.ValidateShareName(shareName);
            if (error != null)
            {
                _powerFault = true;
                UpdateButtons();
                MessageBox.Show(error, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        PowerButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _bridge.Start(printer, share, shareName));
            _powerFault = false;
            SaveSettings(printer);
            await ReloadPrintersAsync();
        }
        catch (Exception ex)
        {
            _powerFault = true;
            MessageBox.Show(ex.Message, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateButtons();
    }

    private async Task StopBridgeAsync()
    {
        PowerButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _bridge.Stop());
            _powerFault = false;
        }
        catch (Exception ex)
        {
            _powerFault = true;
            MessageBox.Show(ex.Message, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool running = _bridge.IsRunning;
        PowerButton.IsEnabled = true;
        PowerButton.Content = running ? "Включено" : "Включить";
        System.Windows.Media.Color fill = running
            ? System.Windows.Media.Color.FromRgb(0x37, 0xA2, 0xEA)
            : _powerFault
                ? System.Windows.Media.Color.FromRgb(0xD7, 0x00, 0x15)
                : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF);
        var animation = new ColorAnimation(fill, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _powerBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        PowerButton.Foreground = new SolidColorBrush(running || _powerFault
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1F));
        _tray.Text = running ? TrimTooltip("AirPrinter, порт " + _bridge.Port) : "AirPrinter";
    }

    private async Task ApplyPrinterChangeAsync(InstalledPrinter printer)
    {
        bool share = ShareBox.IsChecked == true;
        string shareName = ShareNameBox.Text.Trim();
        if (share)
        {
            string? error = PrinterShareService.ValidateShareName(shareName);
            if (error != null)
            {
                AppendLog(error);
                return;
            }
        }

        try
        {
            await Task.Run(() => _bridge.Update(printer, share, shareName));
            SaveSettings(printer);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AirPrinter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettings(InstalledPrinter printer)
    {
        _settings.PrinterName = printer.Name;
        _settings.Share = ShareBox.IsChecked == true;
        _settings.ShareName = ShareNameBox.Text.Trim();
        try
        {
            UserSettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog("Настройки не сохранились: " + ex.GetType().Name);
        }
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        _log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        while (_log.Count > 400)
        {
            _log.RemoveAt(0);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ShowHome_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    private void ShowPrinter_Click(object sender, RoutedEventArgs e) => ShowPage(PrinterPage);

    private void ShowDocs_Click(object sender, RoutedEventArgs e) => ShowPage(DocsPage);

    private void ShowDriver_Click(object sender, RoutedEventArgs e) => ShowPage(DriverPage);

    private void ShowLog_Click(object sender, RoutedEventArgs e) => ShowPage(LogPage);

    private void ShowPage(UIElement page)
    {
        HomePage.Visibility = page == HomePage ? Visibility.Visible : Visibility.Collapsed;
        PrinterPage.Visibility = page == PrinterPage ? Visibility.Visible : Visibility.Collapsed;
        DocsPage.Visibility = page == DocsPage ? Visibility.Visible : Visibility.Collapsed;
        DriverPage.Visibility = page == DriverPage ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = page == LogPage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Power_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        System.Windows.Point point = e.GetPosition(RippleCanvas);
        SpawnRipple(point, 0);
        SpawnRipple(point, 90);
    }

    private void SpawnRipple(System.Windows.Point point, int delayMs)
    {
        var ring = new Ellipse
        {
            Width = 28,
            Height = 28,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 255, 255, 255)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0x37, 0xA2, 0xEA)),
            IsHitTestVisible = false,
            Opacity = 0.8,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.2, 0.2),
        };
        Canvas.SetLeft(ring, point.X - 14);
        Canvas.SetTop(ring, point.Y - 14);
        RippleCanvas.Children.Add(ring);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.2, 7, TimeSpan.FromMilliseconds(720))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
        };
        var fade = new DoubleAnimation(0.75, 0, TimeSpan.FromMilliseconds(720))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
        };
        fade.Completed += (_, _) => RippleCanvas.Children.Remove(ring);
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
        var transform = (ScaleTransform)ring.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge.IsRunning)
        {
            await StopBridgeAsync();
            return;
        }

        await StartBridgeAsync();
    }

    private void ExitApplication()
    {
        _exit = true;
        Close();
    }

    private static string TrimTooltip(string text)
    {
        return text.Length <= 63 ? text : text[..63];
    }

    private static Icon LoadTrayIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        if (info == null)
        {
            throw new InvalidOperationException("Значок приложения не найден.");
        }

        using var stream = info.Stream;
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }
}
