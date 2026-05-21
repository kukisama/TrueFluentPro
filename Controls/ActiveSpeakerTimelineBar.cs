using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Controls
{
    /// <summary>
    /// Teams 风格发言人时间轴：双轨条状能量图，最近 N 秒滚动显示。
    /// 上轨=麦克风（我，#3B82F6 蓝），下轨=环回（对方，#EF4444 红）。
    /// </summary>
    public sealed class ActiveSpeakerTimelineBar : Control
    {
        public static readonly StyledProperty<ActiveSpeakerTimelineStore?> StoreProperty =
            AvaloniaProperty.Register<ActiveSpeakerTimelineBar, ActiveSpeakerTimelineStore?>(nameof(Store));

        public static readonly StyledProperty<double> WindowSecondsProperty =
            AvaloniaProperty.Register<ActiveSpeakerTimelineBar, double>(nameof(WindowSeconds), 60.0);

        public static readonly StyledProperty<bool> IsLiveProperty =
            AvaloniaProperty.Register<ActiveSpeakerTimelineBar, bool>(nameof(IsLive), true);

        public ActiveSpeakerTimelineStore? Store
        {
            get => GetValue(StoreProperty);
            set => SetValue(StoreProperty, value);
        }

        public double WindowSeconds
        {
            get => GetValue(WindowSecondsProperty);
            set => SetValue(WindowSecondsProperty, value);
        }

        /// <summary>
        /// 是否处于"实时"状态。false 时停止滚动、冻结时间基准在最后一帧，并停掉刷新计时器。
        /// </summary>
        public bool IsLive
        {
            get => GetValue(IsLiveProperty);
            set => SetValue(IsLiveProperty, value);
        }

        private static readonly IBrush MicBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
        private static readonly IBrush LoopBrush = new SolidColorBrush(Color.Parse("#EF4444"));
        private static readonly IBrush MicDimBrush = new SolidColorBrush(Color.FromArgb(40, 0x3B, 0x82, 0xF6));
        private static readonly IBrush LoopDimBrush = new SolidColorBrush(Color.FromArgb(40, 0xEF, 0x44, 0x44));
        private static readonly IBrush MicLaneBg = new SolidColorBrush(Color.FromArgb(24, 0x3B, 0x82, 0xF6));
        private static readonly IBrush LoopLaneBg = new SolidColorBrush(Color.FromArgb(24, 0xEF, 0x44, 0x44));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#888888"));
        private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(40, 0x88, 0x88, 0x88)), 0.5);

        private DispatcherTimer? _timer;
        private DateTime? _frozenNow;

        public ActiveSpeakerTimelineBar()
        {
            Height = 54;
            MinHeight = 54;
            ClipToBounds = true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => InvalidateVisual();
            if (IsLive) _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer?.Stop();
            _timer = null;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsLiveProperty)
            {
                var live = change.GetNewValue<bool>();
                if (live)
                {
                    // 重新开始：清空冻结时间，丢弃旧数据让用户从空白开始
                    _frozenNow = null;
                    Store?.Clear();
                    _timer?.Start();
                    InvalidateVisual();
                }
                else
                {
                    // 停止：把当前时间锁定为冻结基准，停掉计时器
                    _frozenNow = DateTime.UtcNow;
                    _timer?.Stop();
                    InvalidateVisual();
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            // 整体两行布局：圆角柔和背景的双轨
            const double labelW = 56;
            const double gap = 6.0;
            var trackX = labelW;
            var trackW = Math.Max(0, w - labelW - 8);

            var laneH = Math.Max(16, (h - gap) / 2.0);
            var micLaneTop = 0.0;
            var loopLaneTop = micLaneTop + laneH + gap;

            // 圆角轨道底
            var micRect = new Rect(trackX, micLaneTop, trackW, laneH);
            var loopRect = new Rect(trackX, loopLaneTop, trackW, laneH);
            context.DrawRectangle(MicLaneBg, null, micRect, 6, 6);
            context.DrawRectangle(LoopLaneBg, null, loopRect, 6, 6);

            // 左侧标签：彩色小圆点 + 名称
            DrawDot(context, new Point(10, micLaneTop + laneH / 2.0), MicBrush);
            DrawDot(context, new Point(10, loopLaneTop + laneH / 2.0), LoopBrush);
            DrawLabel(context, "我", new Point(22, micLaneTop + laneH / 2.0 - 7), MicBrush, 12, true);
            DrawLabel(context, "对方", new Point(22, loopLaneTop + laneH / 2.0 - 7), LoopBrush, 12, true);

            var store = Store;
            if (store == null) return;

            var windowSec = Math.Max(5.0, WindowSeconds);
            var now = _frozenNow ?? DateTime.UtcNow;
            var start = now - TimeSpan.FromSeconds(windowSec);
            var samples = store.GetRange(start, now);
            if (samples.Count == 0) return;

            // 用裁剪区限制竖条画到轨道圆角内
            using (context.PushClip(micRect))
            {
                DrawLaneBars(context, samples, start, windowSec, trackX, trackW,
                    micLaneTop, laneH, useMic: true);
            }
            using (context.PushClip(loopRect))
            {
                DrawLaneBars(context, samples, start, windowSec, trackX, trackW,
                    loopLaneTop, laneH, useMic: false);
            }
        }

        private static void DrawLaneBars(DrawingContext context,
            System.Collections.Generic.IReadOnlyList<ActiveSpeakerSample> samples,
            DateTime start, double windowSec, double trackX, double trackW,
            double laneTop, double laneH, bool useMic)
        {
            double SecToX(DateTime t) => trackX + (t - start).TotalSeconds / windowSec * trackW;
            var pxPerSample = trackW / Math.Max(1, samples.Count);
            var barW = Math.Max(1.0, pxPerSample);

            foreach (var s in samples)
            {
                var x = SecToX(s.Timestamp);
                var rms = useMic ? s.MicEmaRms : s.LoopbackEmaRms;
                var barH = NormalizeDbHeight(rms, laneH);
                if (barH < 0.5) continue;

                var isActive = s.IsLocked &&
                    ((useMic && s.ActiveSource == VadGateController.ActiveSource.Mic) ||
                     (!useMic && s.ActiveSource == VadGateController.ActiveSource.Loopback));
                var brush = useMic
                    ? (isActive ? MicBrush : MicDimBrush)
                    : (isActive ? LoopBrush : LoopDimBrush);

                // 中线对称的小竖条，圆角微弱
                var rect = new Rect(x, laneTop + (laneH - barH) / 2.0, barW, barH);
                context.FillRectangle(brush, rect);
            }
        }

        private static void DrawDot(DrawingContext context, Point center, IBrush brush)
        {
            context.DrawEllipse(brush, null, center, 4, 4);
        }

        private static double RmsToDb(double rms)
        {
            if (rms <= 1e-6) return -120.0;
            return 20.0 * Math.Log10(rms);
        }

        private static double NormalizeDbHeight(double rms, double laneH)
        {
            // -60dB..0dB → 0..laneH
            if (rms <= 1e-6) return 0;
            var db = 20.0 * Math.Log10(rms);
            var normalized = (db + 60.0) / 60.0;
            normalized = Math.Clamp(normalized, 0, 1);
            return normalized * (laneH - 4);
        }

        private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush,
            double size = 12, bool bold = false)
        {
            var typeface = bold
                ? new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold)
                : Typeface.Default;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush);
            context.DrawText(ft, origin);
        }
    }
}
