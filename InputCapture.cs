using System.Runtime.InteropServices;

namespace DeskDock;

public sealed class InputCapture : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmRightDown = 0x0204;
    private const int WmRightUp = 0x0205;
    private const int WmMiddleDown = 0x0207;
    private const int WmMiddleUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int EvSyn = 0;
    private const int EvKey = 1;
    private const int EvRel = 2;
    private const int SynReport = 0;
    private const int RelX = 0;
    private const int RelY = 1;
    private const int RelHorizontalWheel = 6;
    private const int RelWheel = 8;
    private const int ButtonLeft = 272;
    private const int ButtonRight = 273;
    private const int ButtonMiddle = 274;
    private const double PointerSensitivity = 1.6;
    private const uint LlMouseInjected = 0x00000001;
    private const uint LlKeyboardInjected = 0x00000010;
    private const int WheelDelta = 120;

    private readonly Action<string> _sendCommand;
    private readonly Action<int[]> _sendPointerEvents;
    private readonly int _androidWidth;
    private readonly int _androidHeight;
    private readonly int _hostWidth;
    private readonly int _hostHeight;
    private readonly int _centerX;
    private readonly int _centerY;
    private readonly double _scaleX;
    private readonly double _scaleY;
    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HashSet<uint> _swallowedKeys = new();
    private readonly HashSet<int> _pressedMouseButtons = new();
    private readonly HashSet<int> _swallowedMouseButtons = new();
    private readonly object _coordinateLock = new();
    private readonly Timer _moveTimer;
    private LowLevelMouseProc? _mouseProc;
    private LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private int _lastHostX;
    private double _androidX;
    private double _androidY;
    private int _pendingX;
    private int _pendingY;
    private int _sentX = -1;
    private int _sentY = -1;
    private int _verticalWheelRemainder;
    private int _horizontalWheelRemainder;
    private int _cursorShowCalls;
    private bool _isControlling;
    private bool _started;
    private bool _disposed;

    public event Action<bool>? ModeChanged;

    public InputCapture(Action<string> sendCommand, Action<int[]> sendPointerEvents, int androidWidth, int androidHeight)
    {
        _sendCommand = sendCommand;
        _sendPointerEvents = sendPointerEvents;
        _androidWidth = Math.Max(androidWidth, 1);
        _androidHeight = Math.Max(androidHeight, 1);
        _hostWidth = Math.Max(GetSystemMetrics(0), 1);
        _hostHeight = Math.Max(GetSystemMetrics(1), 1);
        _centerX = _hostWidth / 2;
        _centerY = _hostHeight / 2;
        _scaleX = (double)_androidWidth / _hostWidth;
        _scaleY = (double)_androidHeight / _hostHeight;
        GetCursorPos(out var current);
        _lastHostX = current.X;
        _moveTimer = new Timer(FlushPointerPosition, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Start()
    {
        if (_started)
            return true;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
            return false;

        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            return false;
        }

        _started = true;
        _moveTimer.Change(8, 8);
        return true;
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr dataPointer)
    {
        if (code < 0)
            return CallNextHookEx(_mouseHook, code, message, dataPointer);

        try
        {
            var data = Marshal.PtrToStructure<MouseHookData>(dataPointer);
            if ((data.Flags & LlMouseInjected) != 0)
                return CallNextHookEx(_mouseHook, code, message, dataPointer);

            var messageId = message.ToInt32();
            if (!_isControlling && (messageId is WmLeftUp or WmRightUp or WmMiddleUp) && _swallowedMouseButtons.Remove(messageId - 1))
                return new IntPtr(1);

            if (!_isControlling)
            {
                if (messageId == WmMouseMove)
                {
                    var movingRight = data.Point.X > _lastHostX;
                    var crossedRightEdge = movingRight && data.Point.X >= _hostWidth - 1;
                    _lastHostX = data.Point.X;
                    if (crossedRightEdge)
                    {
                        EnterAndroid(data.Point.Y);
                        return new IntPtr(1);
                    }
                }
                return CallNextHookEx(_mouseHook, code, message, dataPointer);
            }

            switch (messageId)
            {
                case WmMouseMove:
                    HandleControlledMove(data.Point);
                    return new IntPtr(1);
                case WmLeftDown:
                    _pressedMouseButtons.Add(WmLeftDown);
                    _swallowedMouseButtons.Add(WmLeftDown);
                    SendPointerButton(ButtonLeft, isDown: true);
                    return new IntPtr(1);
                case WmLeftUp:
                    _pressedMouseButtons.Remove(WmLeftDown);
                    _swallowedMouseButtons.Remove(WmLeftDown);
                    SendPointerButton(ButtonLeft, isDown: false);
                    return new IntPtr(1);
                case WmRightDown:
                    _swallowedMouseButtons.Add(WmRightDown);
                    _pressedMouseButtons.Add(WmRightDown);
                    SendPointerButton(ButtonRight, isDown: true);
                    return new IntPtr(1);
                case WmRightUp:
                    _swallowedMouseButtons.Remove(WmRightDown);
                    _pressedMouseButtons.Remove(WmRightDown);
                    SendPointerButton(ButtonRight, isDown: false);
                    return new IntPtr(1);
                case WmMiddleDown:
                case WmMiddleUp:
                    if (messageId == WmMiddleDown)
                    {
                        _swallowedMouseButtons.Add(WmMiddleDown);
                        _pressedMouseButtons.Add(WmMiddleDown);
                        SendPointerButton(ButtonMiddle, isDown: true);
                    }
                    else
                    {
                        _swallowedMouseButtons.Remove(WmMiddleDown);
                        _pressedMouseButtons.Remove(WmMiddleDown);
                        SendPointerButton(ButtonMiddle, isDown: false);
                    }
                    return new IntPtr(1);
                case WmMouseWheel:
                case WmMouseHWheel:
                    HandleWheel(data.MouseData, messageId == WmMouseHWheel);
                    return new IntPtr(1);
                default:
                    return CallNextHookEx(_mouseHook, code, message, dataPointer);
            }
        }
        catch
        {
            return _isControlling ? new IntPtr(1) : CallNextHookEx(_mouseHook, code, message, dataPointer);
        }
    }

    private void HandleControlledMove(POINT point)
    {
        var dx = point.X - _centerX;
        var dy = point.Y - _centerY;
        if (dx == 0 && dy == 0)
            return;

        var exitToWindows = false;
        lock (_coordinateLock)
        {
            var nextX = _androidX + dx * _scaleX * PointerSensitivity;
            var nextY = _androidY + dy * _scaleY * PointerSensitivity;
            if (dx < 0 && nextX < 0)
            {
                exitToWindows = true;
            }
            else
            {
                _androidX = Math.Clamp(nextX, 0, _androidWidth - 1);
                _androidY = Math.Clamp(nextY, 0, _androidHeight - 1);
                _pendingX = (int)Math.Round(_androidX);
                _pendingY = (int)Math.Round(_androidY);
            }
        }

        if (exitToWindows)
        {
            ExitToWindows();
            return;
        }

        SetCursorPos(_centerX, _centerY);
    }

    private void EnterAndroid(int hostY)
    {
        int initialY;
        lock (_coordinateLock)
        {
            _androidX = 0;
            _androidY = Math.Clamp(hostY, 0, _hostHeight - 1) * _scaleY;
            _pendingX = 0;
            _pendingY = (int)Math.Round(Math.Clamp(_androidY, 0, _androidHeight - 1));
            initialY = _pendingY;
            _sentX = 0;
            _sentY = initialY;
        }

        _isControlling = true;
        HideSystemCursor();
        SetCursorPos(_centerX, _centerY);
        _sendPointerEvents(new[]
        {
            EvRel, RelX, -20000,
            EvRel, RelY, -20000,
            EvSyn, SynReport, 0,
            EvRel, RelY, initialY,
            EvSyn, SynReport, 0
        });
        ModeChanged?.Invoke(true);
    }

    private void ExitToWindows()
    {
        if (!_isControlling)
            return;

        int androidY;
        lock (_coordinateLock)
            androidY = (int)Math.Round(_androidY);

        if (_pressedMouseButtons.Contains(WmLeftDown))
            SendPointerButton(ButtonLeft, isDown: false);
        if (_pressedMouseButtons.Contains(WmRightDown))
            SendPointerButton(ButtonRight, isDown: false);
        if (_pressedMouseButtons.Contains(WmMiddleDown))
            SendPointerButton(ButtonMiddle, isDown: false);
        _pressedMouseButtons.Clear();
        _isControlling = false;
        ShowSystemCursor();
        var hostY = (int)Math.Round((double)androidY / Math.Max(_androidHeight - 1, 1) * (_hostHeight - 1));
        SetCursorPos(_hostWidth - 2, Math.Clamp(hostY, 0, _hostHeight - 1));
        _lastHostX = _hostWidth - 2;
        ModeChanged?.Invoke(false);
    }

    private void FlushPointerPosition(object? state)
    {
        if (!_isControlling)
            return;
        SendLatestMove();
    }

    private void SendLatestMove()
    {
        int deltaX;
        int deltaY;
        lock (_coordinateLock)
        {
            if (_pendingX == _sentX && _pendingY == _sentY)
                return;
            deltaX = _pendingX - _sentX;
            deltaY = _pendingY - _sentY;
            _sentX = _pendingX;
            _sentY = _pendingY;
        }

        _sendPointerEvents(new[]
        {
            EvRel, RelX, deltaX,
            EvRel, RelY, deltaY,
            EvSyn, SynReport, 0
        });
    }

    private void SendPointerButton(int button, bool isDown) => _sendPointerEvents(new[]
    {
        EvKey, button, isDown ? 1 : 0,
        EvSyn, SynReport, 0
    });

    private void HandleWheel(uint mouseData, bool horizontal)
    {
        var delta = unchecked((short)(mouseData >> 16));
        if (delta == 0)
            return;

        ref var remainder = ref (horizontal ? ref _horizontalWheelRemainder : ref _verticalWheelRemainder);
        var accumulated = remainder + delta;
        var notches = accumulated / WheelDelta;
        remainder = accumulated - notches * WheelDelta;
        if (notches == 0)
            return;

        _sendPointerEvents(new[]
        {
            EvRel, horizontal ? RelHorizontalWheel : RelWheel, notches,
            EvSyn, SynReport, 0
        });
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr dataPointer)
    {
        if (code < 0)
            return CallNextHookEx(_keyboardHook, code, message, dataPointer);

        try
        {
            var data = Marshal.PtrToStructure<KeyboardHookData>(dataPointer);
            if ((data.Flags & LlKeyboardInjected) != 0)
                return CallNextHookEx(_keyboardHook, code, message, dataPointer);

            var messageId = message.ToInt32();
            var isDown = messageId is WmKeyDown or WmSysKeyDown;
            var isUp = messageId is WmKeyUp or WmSysKeyUp;
            if (!isDown && !isUp)
                return CallNextHookEx(_keyboardHook, code, message, dataPointer);

            if (!isDown)
            {
                var wasSwallowed = _swallowedKeys.Remove(data.VirtualKey);
                _pressedKeys.Remove(data.VirtualKey);
                return wasSwallowed || _isControlling ? new IntPtr(1) : CallNextHookEx(_keyboardHook, code, message, dataPointer);
            }

            if (_swallowedKeys.Contains(data.VirtualKey))
                return new IntPtr(1);
            _pressedKeys.Add(data.VirtualKey);
            if (!_isControlling)
                return CallNextHookEx(_keyboardHook, code, message, dataPointer);
            _swallowedKeys.Add(data.VirtualKey);

            if (data.VirtualKey == 0x7B && _pressedKeys.Any(KeyCodeMap.IsControl) && _pressedKeys.Any(KeyCodeMap.IsAlt))
            {
                _pressedKeys.Clear();
                ExitToWindows();
                return new IntPtr(1);
            }

            if (KeyCodeMap.IsModifier(data.VirtualKey))
                return new IntPtr(1);

            var keyCode = KeyCodeMap.AndroidCode(data.VirtualKey);
            if (keyCode is null)
                return new IntPtr(1);

            var modifiers = _pressedKeys.Select(KeyCodeMap.ModifierCode).Where(value => value is not null).Distinct().ToList();
            if (modifiers.Count == 0)
                _sendCommand($"input keyevent {keyCode}");
            else
                _sendCommand($"input keycombination {string.Join(' ', modifiers.Append(keyCode))}");

            return new IntPtr(1);
        }
        catch
        {
            return _isControlling ? new IntPtr(1) : CallNextHookEx(_keyboardHook, code, message, dataPointer);
        }
    }

    private void HideSystemCursor()
    {
        _cursorShowCalls = 0;
        int visibility;
        do
        {
            visibility = ShowCursor(false);
            _cursorShowCalls++;
        } while (visibility >= 0 && _cursorShowCalls < 16);
    }

    private void ShowSystemCursor()
    {
        while (_cursorShowCalls > 0)
        {
            ShowCursor(true);
            _cursorShowCalls--;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ExitToWindows();
        _moveTimer.Dispose();
        if (_mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = IntPtr.Zero;
        _keyboardHook = IntPtr.Zero;
        _started = false;
        GC.KeepAlive(_mouseProc);
        GC.KeepAlive(_keyboardProc);
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);
}
