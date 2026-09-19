using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace TWChatOverlay.Services
{
    /// <summary>
    /// 게임(테일즈위버) 창을 설정의 TopmostGuardProcessNames로 찾아 위치/가시성을 100ms 주기로 감시한다.
    /// GetWindowRect는 물리 픽셀 좌표를 반환하므로, 창이 있는 모니터의 DPI로 나눠 WPF DIP 좌표로 변환해 제공한다.
    /// (app.manifest가 Per-Monitor V2 DPI 인식을 선언하고 있어 WPF Window.Left/Top은 DIP 기준이다.)
    /// ForegroundTopmostGuard와 달리 게임이 전경이 아니어도(창이 존재하고 최소화만 안 되어 있으면) 추적한다.
    /// </summary>
    public static class GameWindowTracker
    {
        private static DispatcherTimer? _timer;
        private static IntPtr _gameHwnd = IntPtr.Zero;
        private static Rect? _currentRect;
        private static bool _isAvailable;
        private static bool _initialized;

        /// <summary>추적 중인 게임 창의 위치/크기가 바뀔 때마다 발생 (WPF DIP 좌표).</summary>
        public static event Action<Rect>? RectChanged;

        /// <summary>게임 창을 찾았고(존재 + 최소화 아님) 사용 가능한 상태가 바뀔 때 발생.</summary>
        public static event Action<bool>? AvailabilityChanged;

        /// <summary>마지막으로 확인된 게임 창의 사각형(WPF DIP). 아직 못 찾았으면 null.</summary>
        public static Rect? CurrentRect => _currentRect;

        public static bool IsAvailable => _isAvailable;

        /// <summary>App.OnStartup에서 한 번 호출한다. 이후 100ms 주기로 자동 동작.</summary>
        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        private static void Tick()
        {
            try
            {
                // 부착 기능이 꺼져 있으면 프로세스 스캔조차 하지 않는다 (불필요한 부하 방지)
                var settings = ToastPresentationHelper.FindSharedSettings();
                if (settings == null || !settings.AttachOverlaysToGameWindow)
                {
                    if (_isAvailable)
                        SetAvailability(false);
                    return;
                }

                if (_gameHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_gameHwnd))
                {
                    _gameHwnd = FindGameWindow(settings.TopmostGuardProcessNames);
                }

                if (_gameHwnd == IntPtr.Zero)
                {
                    SetAvailability(false);
                    return;
                }

                if (NativeMethods.IsIconic(_gameHwnd))
                {
                    SetAvailability(false);
                    return;
                }

                // 일부 게임(특히 테두리 없는 전체화면/보더리스 모드)은 Alt+Tab이나 최소화 시
                // 진짜로 최소화(IsIconic)되지 않고 그냥 창을 숨기기만(WS_VISIBLE 해제) 한다.
                // 그런 경우까지 잡으려면 가시성도 같이 확인해야 한다.
                if (!NativeMethods.IsWindowVisible(_gameHwnd))
                {
                    SetAvailability(false);
                    return;
                }

                OverlayHelper.RECT physicalRect = OverlayHelper.GetActualRect(_gameHwnd);
                if (physicalRect.Width <= 0 || physicalRect.Height <= 0)
                {
                    SetAvailability(false);
                    return;
                }

                double scale = GetDpiScale(_gameHwnd);
                var dipRect = new Rect(
                    physicalRect.Left / scale,
                    physicalRect.Top / scale,
                    physicalRect.Width / scale,
                    physicalRect.Height / scale);

                SetAvailability(true);

                if (_currentRect is not { } previous ||
                    Math.Abs(previous.Left - dipRect.Left) > 0.5 ||
                    Math.Abs(previous.Top - dipRect.Top) > 0.5 ||
                    Math.Abs(previous.Width - dipRect.Width) > 0.5 ||
                    Math.Abs(previous.Height - dipRect.Height) > 0.5)
                {
                    _currentRect = dipRect;
                    RectChanged?.Invoke(dipRect);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GameWindowTracker tick failed.", ex);
            }
        }

        private static double GetDpiScale(IntPtr hwnd)
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForWindow(hwnd);
                return dpi == 0 ? 1.0 : dpi / 96.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private static void SetAvailability(bool available)
        {
            if (_isAvailable == available)
                return;

            _isAvailable = available;
            if (!available)
                _currentRect = null;

            AvailabilityChanged?.Invoke(available);
        }

        /// <summary>쉼표/세미콜론/공백으로 구분된 프로세스 이름 목록에서 첫 번째로 찾은 창을 반환한다.</summary>
        private static IntPtr FindGameWindow(string? processNames)
        {
            string[] names = (processNames ?? string.Empty)
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (names.Length == 0)
                names = new[] { "Talesweaver" };

            foreach (string rawName in names)
            {
                string name = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? rawName[..^4]
                    : rawName;

                Process[]? processes = null;
                try
                {
                    processes = Process.GetProcessesByName(name);
                    foreach (Process process in processes)
                    {
                        try
                        {
                            process.Refresh();
                            IntPtr handle = process.MainWindowHandle;
                            if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
                                return handle;
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                catch
                {
                    // 프로세스 열거 실패는 무시하고 다음 이름으로 넘어간다
                }
            }

            return IntPtr.Zero;
        }
    }
}
