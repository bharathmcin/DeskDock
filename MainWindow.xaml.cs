using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskDock;

public partial class MainWindow : Window
{
    private AdbClient? _adb;
    private InputCapture? _inputCapture;
    private int _androidWidth;
    private int _androidHeight;

    public MainWindow()
    {
        InitializeComponent();
        AdbPathBox.Text = AdbClient.FindAdb() ?? "";
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (!TryGetAdbPath(out var adbPath))
        {
            ToastText.Text = "Install Android SDK Platform-Tools, then select adb.exe.";
            return;
        }

        SetBusy(true);
        ToastText.Text = "Looking for USB devices...";
        try
        {
            var previousSerial = (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;
            var devices = await AdbClient.ListDevicesAsync(adbPath);
            DeviceComboBox.ItemsSource = devices;
            DeviceComboBox.SelectedItem = devices.FirstOrDefault(device => device.Serial == previousSerial)
                ?? devices.FirstOrDefault(device => device.State == "device");

            var available = devices.Count(device => device.State == "device");
            ToastText.Text = available switch
            {
                0 when devices.Any(device => device.State == "unauthorized") => "Unlock Android and accept the USB debugging prompt.",
                0 => "No authorized Android device found. Check the USB cable and debugging setting.",
                1 => "Android device is ready to connect.",
                _ => $"Found {available} authorized Android devices."
            };
        }
        catch (Exception exception)
        {
            ToastText.Text = FriendlyMessage(exception);
            DeviceComboBox.ItemsSource = Array.Empty<AndroidDevice>();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adb is not null)
        {
            Disconnect();
            return;
        }

        if (!TryGetAdbPath(out var adbPath))
        {
            ToastText.Text = "Select a valid adb.exe first.";
            return;
        }

        if (DeviceComboBox.SelectedItem is not AndroidDevice { State: "device" } device)
        {
            ToastText.Text = "Select an authorized Android device. Accept the USB debugging prompt if it appears.";
            return;
        }

        SetBusy(true);
        ToastText.Text = "Connecting over USB...";
        try
        {
            (_androidWidth, _androidHeight) = await AdbClient.ReadDisplaySizeAsync(adbPath, device.Serial);

            var client = new AdbClient(adbPath, device.Serial);
            client.ShellOutput += OnShellOutput;
            try
            {
                client.StartShell();
                await client.StartPointerDeviceAsync();
                _adb = client;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            ConnectionDot.Fill = (Brush)FindResource("AccentBrush");
            ConnectionStateText.Text = $"Connected · {device.Serial}";
            ConnectionStateText.Foreground = (Brush)FindResource("TextBrush");
            DeviceResolutionText.Text = $"Android display · {_androidWidth} × {_androidHeight}";
            ConnectButton.Content = "Disconnect";
            DeviceComboBox.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            AdbPathBox.IsEnabled = false;
            ControlButton.IsEnabled = true;
            ModeText.Text = "Ready. Start sharing, then cross the right edge of the PC display.";
            ToastText.Text = "USB debugging connection is ready.";
        }
        catch (Exception exception)
        {
            ToastText.Text = FriendlyMessage(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inputCapture is not null)
        {
            StopSharing();
            return;
        }

        if (_adb is null)
            return;

        var capture = new InputCapture(_adb.SendCommand, _adb.SendPointerEvents, _androidWidth, _androidHeight);
        capture.ModeChanged += OnModeChanged;
        if (!capture.Start())
        {
            capture.Dispose();
            ToastText.Text = "Windows could not start the global mouse and keyboard hooks.";
            return;
        }

        _inputCapture = capture;
        ControlButton.Content = "Stop sharing";
        ToastText.Text = "Move to the right edge to enter Android. Ctrl+Alt+F12 always releases the mouse.";
        ModeText.Text = "Windows is active. Move the pointer through the right edge to enter Android.";
    }

    private void OnModeChanged(bool controllingAndroid)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_inputCapture is null)
                return;

            ModeText.Text = controllingAndroid
                ? "Android is active. Move left through its edge to return, or press Ctrl + Alt + F12."
                : "Windows is active. Move the pointer through the right edge to enter Android.";
        });
    }

    private void OnShellOutput(string line, bool isError)
    {
        if (!isError || string.IsNullOrWhiteSpace(line))
            return;

        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => ToastText.Text = line.Trim());
        }
    }

    private void StopSharing()
    {
        _inputCapture?.Dispose();
        _inputCapture = null;
        ControlButton.Content = "Start sharing";
        ModeText.Text = _adb is null
            ? "Connect your Android device to begin."
            : "Sharing stopped. Start again when you are ready.";
    }

    private void Disconnect()
    {
        StopSharing();
        _adb?.Dispose();
        _adb = null;
        ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(227, 168, 107));
        ConnectionStateText.Text = "Not connected";
        ConnectionStateText.Foreground = (Brush)FindResource("MutedBrush");
        DeviceResolutionText.Text = "Connect an Android device by USB";
        ConnectButton.Content = "Connect over USB";
        DeviceComboBox.IsEnabled = true;
        RefreshButton.IsEnabled = true;
        AdbPathBox.IsEnabled = true;
        ControlButton.IsEnabled = false;
        ToastText.Text = "USB connection closed.";
    }

    private bool TryGetAdbPath(out string path)
    {
        path = AdbPathBox.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            path = AdbClient.FindAdb() ?? "";
            if (!string.IsNullOrEmpty(path))
                AdbPathBox.Text = path;
        }
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string FriendlyMessage(Exception exception)
    {
        var message = exception.Message.Trim();
        if (message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "Unlock Android and accept the USB debugging authorization prompt.";
        if (message.Contains("no devices", StringComparison.OrdinalIgnoreCase))
            return "No device found. Check the USB cable and USB debugging setting.";
        if (exception is System.TimeoutException or OperationCanceledException)
            return "ADB timed out. Reconnect the USB cable and refresh devices.";
        return message;
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy && _adb is null;
        ConnectButton.IsEnabled = !busy;
        DeviceComboBox.IsEnabled = !busy && _adb is null;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Android Debug Bridge",
            Filter = "ADB executable (adb.exe)|adb.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            AdbPathBox.Text = dialog.FileName;
            _ = RefreshDevicesAsync();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _inputCapture?.Dispose();
        _adb?.Dispose();
    }
}
