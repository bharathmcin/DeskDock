using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Channels;

namespace DeskDock;

public sealed class AdbClient : IDisposable
{
    private const int PointerDeviceId = 88;
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<string> _pointerCommands = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _shell;
    private StreamWriter? _shellInput;
    private Task? _writerTask;
    private Process? _pointerShell;
    private StreamWriter? _pointerShellInput;
    private Task? _pointerWriterTask;
    private bool _disposed;

    public event Action<string, bool>? ShellOutput;

    public AdbClient(string adbPath, string serial)
    {
        _adbPath = adbPath;
        _serial = serial;
    }

    public static string? FindAdb()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), "adb.exe")));
        }

        var sdkRoots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        };
        candidates.AddRange(sdkRoots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root!, "platform-tools", "adb.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<IReadOnlyList<AndroidDevice>> ListDevicesAsync(string adbPath, CancellationToken cancellationToken = default)
    {
        var output = await RunAdbAsync(adbPath, cancellationToken, "devices", "-l");
        var devices = new List<AndroidDevice>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
                continue;

            var details = columns.Skip(2)
                .FirstOrDefault(column => column.StartsWith("model:", StringComparison.Ordinal))?
                .Replace("model:", "", StringComparison.Ordinal)
                .Replace('_', ' ') ?? "Android device";
            devices.Add(new AndroidDevice(columns[0], columns[1], details));
        }

        return devices;
    }

    public static async Task<(int Width, int Height)> ReadDisplaySizeAsync(string adbPath, string serial, CancellationToken cancellationToken = default)
    {
        var output = await RunAdbAsync(adbPath, cancellationToken, "-s", serial, "shell", "wm", "size");
        var matches = Regex.Matches(output, @"(?<width>\d+)x(?<height>\d+)");
        if (matches.Count == 0)
            throw new InvalidOperationException("Android did not report its display size.");

        var match = matches[^1];
        return (int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture));
    }

    public void StartShell()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shell is not null)
            return;

        var startInfo = CreateStartInfo("-s", _serial, "shell");
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        _shell = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start adb shell.");
        _shellInput = _shell.StandardInput;
        _ = DrainOutputAsync(_shell.StandardOutput, isError: false);
        _ = DrainOutputAsync(_shell.StandardError, isError: true);
        _writerTask = Task.Run(WriteCommandsAsync);
    }

    public async Task StartPointerDeviceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pointerShell is not null)
            return;

        var startInfo = CreateStartInfo("-s", _serial, "shell", "uinput", "-");
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        _pointerShell = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Android's virtual mouse.");
        _pointerShellInput = _pointerShell.StandardInput;
        _ = DrainOutputAsync(_pointerShell.StandardOutput, isError: true);
        _ = DrainOutputAsync(_pointerShell.StandardError, isError: true);

        var registration = JsonSerializer.Serialize(new
        {
            id = PointerDeviceId,
            command = "register",
            name = "DeskDock Mouse",
            vid = 0x18d2,
            pid = 0x4ee7,
            bus = "usb",
            configuration = new[]
            {
                new { type = 100, data = new[] { 1, 2 } },
                new { type = 101, data = new[] { 272, 273, 274 } },
                new { type = 102, data = new[] { 0, 1, 6, 8 } }
            }
        });

        await _pointerShellInput.WriteLineAsync(registration.AsMemory(), cancellationToken);
        await _pointerShellInput.FlushAsync(cancellationToken);
        _pointerWriterTask = Task.Run(WritePointerCommandsAsync);

        // Let Android register the virtual device before the app starts sending events.
        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
        if (_pointerShell.HasExited)
            throw new InvalidOperationException("Android stopped the virtual mouse device.");
    }

    public void SendPointerEvents(params int[] events)
    {
        if (_disposed || events.Length == 0 || events.Length % 3 != 0)
            return;

        var command = JsonSerializer.Serialize(new { id = PointerDeviceId, command = "inject", events });
        _pointerCommands.Writer.TryWrite(command);
    }

    public void SendCommand(string command)
    {
        if (!_disposed)
            _commands.Writer.TryWrite(command);
    }

    private async Task WriteCommandsAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token))
            {
                if (_shell is null || _shell.HasExited || _shellInput is null)
                    break;

                await _shellInput.WriteLineAsync(command);
                await _shellInput.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException exception)
        {
            ShellOutput?.Invoke(exception.Message, true);
        }
        catch (InvalidOperationException exception)
        {
            ShellOutput?.Invoke(exception.Message, true);
        }
    }

    private async Task WritePointerCommandsAsync()
    {
        try
        {
            await foreach (var command in _pointerCommands.Reader.ReadAllAsync())
            {
                if (_pointerShell is null || _pointerShell.HasExited || _pointerShellInput is null)
                    break;

                await _pointerShellInput.WriteLineAsync(command);
                await _pointerShellInput.FlushAsync();
            }
        }
        catch (IOException exception)
        {
            ShellOutput?.Invoke(exception.Message, true);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException exception)
        {
            ShellOutput?.Invoke(exception.Message, true);
        }
    }

    private async Task DrainOutputAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ShellOutput?.Invoke(line, isError);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<string> RunAdbAsync(string adbPath, CancellationToken cancellationToken, params string[] arguments)
    {
        if (!File.Exists(adbPath))
            throw new FileNotFoundException("Could not find adb.exe. Install Android SDK Platform-Tools or choose adb.exe.", adbPath);

        using var process = Process.Start(CreateStartInfoForPath(adbPath, arguments))
            ?? throw new InvalidOperationException("Could not start adb.exe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private ProcessStartInfo CreateStartInfo(params string[] arguments) => CreateStartInfoForPath(_adbPath, arguments);

    private static ProcessStartInfo CreateStartInfoForPath(string adbPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = adbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _commands.Writer.TryWrite("exit");
        _commands.Writer.TryComplete();

        _pointerCommands.Writer.TryComplete();
        try
        {
            _pointerWriterTask?.Wait(800);
        }
        catch (AggregateException)
        {
        }

        try
        {
            _pointerShellInput?.Close();
            if (_pointerShell is { HasExited: false } pointerShell && !pointerShell.WaitForExit(1200))
                pointerShell.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _lifetime.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            if (_shell is { HasExited: false } shell && !shell.WaitForExit(1800))
                shell.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _shell?.Dispose();
        _pointerShell?.Dispose();
        _lifetime.Dispose();
    }
}
