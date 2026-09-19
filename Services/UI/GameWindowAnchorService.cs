using System;
using System.Collections.Generic;
using System.Windows;

namespace TWChatOverlay.Services
{
    /// <summary>
    /// 오버레이 창을 게임 창 기준 오프셋(dx, dy)에 등록해 두면, GameWindowTracker가 게임 창 이동을
    /// 감지할 때마다 등록된 창들의 위치를 자동으로 맞춰준다. 게임 창을 찾지 못하거나 최소화되어
    /// 있으면 등록된 창을 임시로 숨긴다(Opacity 0 / IsHitTestVisible false — 창 자신의 Show/Close
    /// 생명주기는 건드리지 않는다).
    ///
    /// 각 오버레이 창은 생성자(또는 Loaded)에서 Attach(this)를, OnClosed에서 Detach(this)를 부르고,
    /// 드래그가 끝나는 시점(위치를 설정에 저장하는 바로 그 지점)에 UpdateOffsetFromCurrentPosition(this)를
    /// 불러 새 오프셋을 반영하면 된다. 부착 기능 자체가 꺼져 있으면(_settings.AttachOverlaysToGameWindow
    /// == false) 아무 것도 하지 않으므로 기존 절대좌표 동작에 영향이 없다.
    /// </summary>
    public static class GameWindowAnchorService
    {
        private sealed class AnchorEntry
        {
            public double OffsetX;
            public double OffsetY;
            public double NormalOpacity = 1.0;
            public bool HiddenByUnavailability;
        }

        private static readonly Dictionary<Window, AnchorEntry> _entries = new();
        private static bool _subscribed;

        private static void EnsureSubscribed()
        {
            if (_subscribed)
                return;
            _subscribed = true;

            GameWindowTracker.RectChanged += OnGameRectChanged;
            GameWindowTracker.AvailabilityChanged += OnGameAvailabilityChanged;
        }

        /// <summary>창을 등록한다. 오프셋은 창의 현재 위치와 게임 창 위치의 차이로 자동 계산한다.</summary>
        public static void Attach(Window window)
        {
            if (window == null)
                return;

            EnsureSubscribed();

            var entry = new AnchorEntry { NormalOpacity = window.Opacity > 0 ? window.Opacity : 1.0 };
            _entries[window] = entry;
            RecomputeOffset(window, entry);

            window.Closed -= Window_Closed;
            window.Closed += Window_Closed;
        }

        public static void Detach(Window window)
        {
            if (window == null)
                return;

            _entries.Remove(window);
            window.Closed -= Window_Closed;
        }

        private static void Window_Closed(object? sender, EventArgs e)
        {
            if (sender is Window window)
                Detach(window);
        }

        /// <summary>
        /// 사용자가 드래그로 창을 옮긴 직후 호출: 현재 Left/Top과 현재 게임 창 위치의 차이를
        /// 새 오프셋으로 저장한다. 게임 창을 못 찾았으면 오프셋을 바꾸지 않는다(직전 값 유지).
        /// </summary>
        public static void UpdateOffsetFromCurrentPosition(Window window)
        {
            if (window == null || !_entries.TryGetValue(window, out var entry))
                return;

            RecomputeOffset(window, entry);
        }

        private static void RecomputeOffset(Window window, AnchorEntry entry)
        {
            if (GameWindowTracker.CurrentRect is not { } gameRect)
                return;

            entry.OffsetX = window.Left - gameRect.Left;
            entry.OffsetY = window.Top - gameRect.Top;
        }

        private static void OnGameRectChanged(Rect gameRect)
        {
            var settings = ToastPresentationHelper.FindSharedSettings();
            if (settings?.AttachOverlaysToGameWindow != true)
                return;

            foreach (KeyValuePair<Window, AnchorEntry> pair in _entries)
            {
                Window window = pair.Key;
                AnchorEntry entry = pair.Value;

                if (entry.HiddenByUnavailability)
                    continue;

                double targetLeft = gameRect.Left + entry.OffsetX;
                double targetTop = gameRect.Top + entry.OffsetY;

                try
                {
                    if (Math.Abs(window.Left - targetLeft) > 0.5)
                        window.Left = targetLeft;
                    if (Math.Abs(window.Top - targetTop) > 0.5)
                        window.Top = targetTop;
                }
                catch
                {
                    // 창이 닫히는 중일 수 있음 — 무시
                }
            }
        }

        private static void OnGameAvailabilityChanged(bool isAvailable)
        {
            var settings = ToastPresentationHelper.FindSharedSettings();
            if (settings?.AttachOverlaysToGameWindow != true)
                return;

            foreach (KeyValuePair<Window, AnchorEntry> pair in _entries)
            {
                Window window = pair.Key;
                AnchorEntry entry = pair.Value;

                try
                {
                    if (!isAvailable)
                    {
                        if (window.Opacity > 0)
                            entry.NormalOpacity = window.Opacity;

                        entry.HiddenByUnavailability = true;
                        window.Opacity = 0;
                        window.IsHitTestVisible = false;
                    }
                    else if (entry.HiddenByUnavailability)
                    {
                        entry.HiddenByUnavailability = false;
                        window.Opacity = entry.NormalOpacity;
                        window.IsHitTestVisible = true;

                        // 숨어 있던 동안 게임 창이 움직였을 수 있으니 복귀 즉시 현재 오프셋 기준으로 한 번 맞춘다
                        if (GameWindowTracker.CurrentRect is { } gameRect)
                        {
                            window.Left = gameRect.Left + entry.OffsetX;
                            window.Top = gameRect.Top + entry.OffsetY;
                        }
                    }
                }
                catch
                {
                    // 창이 닫히는 중일 수 있음 — 무시
                }
            }
        }
    }
}
