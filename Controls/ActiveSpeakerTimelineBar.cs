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

        private static readonly IBrush MicBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
        private static readonly IBrush LoopBrush = new SolidColorBrush(Color.Parse("#EF4444"));
        private static readonly IBrush MicDimBrush = new SolidColorBrush(Color.FromArgb(40, 0x3B, 0x82, 0xF6));
        private static readonly IBrush LoopDimBrush = new SolidColorBrush(Color.FromArgb(40, 0xEF, 0x44, 0x44));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#888888"));
        private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(40, 0x88, 0x88, 0x88)), 0.5);

        private DispatcherTimer? _timer;

        public ActiveSpeakerTimelineBar()
        {
            Height = 44;
            MinHeight = 44;
            ClipToBounds = true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => InvalidateVisual();
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer?.Stop();
            _timer = null;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            // 背景
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)), new Rect(0, 0, w, h));

            // 标签宽度
            const double labelW = 56;
            var trackX = labelW;
            var trackW = Math.Max(0, w - labelW - 8);

            var laneH = (h - 6) / 2.0;
            var micLaneTop = 2.0;
            var loopLaneTop = micLaneTop + laneH + 2.0;

            // 标签
            DrawLabel(context, "🎤 我", new Point(8, micLaneTop + laneH / 2 - 7), MicBrush);
            DrawLabel(context, "🔊 对方", new Point(8, loopLaneTop + laneH / 2 - 7), LoopBrush);

            // 轨道底
            context.FillRectangle(MicDimBrush, new Rect(trackX, micLaneTop, trackW, laneH));
            context.FillRectangle(LoopDimBrush, new Rect(trackX, loopLaneTop, trackW, laneH));

            var store = Store;
            if (store == null) return;

            var windowSec = Math.Max(5.0, WindowSeconds);
            var now = DateTime.UtcNow;
            var start = now - TimeSpan.FromSeconds(windowSec);
            var samples = store.GetRange(start, now);
            if (samples.Count == 0)
            {
                // 提示：没有采样（VAD 未启用 / 未启动翻译 / 双路未开）
                DrawLabel(context, "等待采样…", new Point(trackX + 4, micLaneTop + laneH / 2 - 7), LabelBrush);
                return;
            }

            // 每个 sample 占的像素宽（按时间比例）
            double SecToX(DateTime t) => trackX + (t - start).TotalSeconds / windowSec * trackW;

            // 把相邻同状态采样压成一段连续 bar 以减少绘制
            // 简化：每个采样画一个等高条
            var pxPerSample = trackW / Math.Max(1, samples.Count);
            var barW = Math.Max(1.0, pxPerSample);

            foreach (var s in samples)
            {
                var x = SecToX(s.Timestamp);

                // 麦克风轨
                var micH = NormalizeDbHeight(s.MicEmaRms, laneH);
                if (micH > 0.5)
                {
                    var brush = (s.IsLocked && s.ActiveSource == VadGateController.ActiveSource.Mic) ? MicBrush : MicDimBrush;
                    context.FillRectangle(brush, new Rect(x, micLaneTop + (laneH - micH), barW, micH));
                }

                // 环回轨
                var loopH = NormalizeDbHeight(s.LoopbackEmaRms, laneH);
                if (loopH > 0.5)
                {
                    var brush = (s.IsLocked && s.ActiveSource == VadGateController.ActiveSource.Loopback) ? LoopBrush : LoopDimBrush;
                    context.FillRectangle(brush, new Rect(x, loopLaneTop + (laneH - loopH), barW, loopH));
                }
            }

            // 中线
            context.DrawLine(GridPen, new Point(trackX, micLaneTop + laneH + 1),
                new Point(trackX + trackW, micLaneTop + laneH + 1));

            // 右上角实时 dB 数值（最近一个采样）
            var last = samples[samples.Count - 1];
            var micDb = RmsToDb(last.MicEmaRms);
            var loopDb = RmsToDb(last.LoopbackEmaRms);
            var lockedTag = last.IsLocked
                ? (last.ActiveSource == VadGateController.ActiveSource.Mic ? "🔒我" : "🔒对方")
                : "未锁";
            DrawLabel(context, $"我 {micDb:F0}dB", new Point(w - 130, micLaneTop), MicBrush);
            DrawLabel(context, $"对方 {loopDb:F0}dB  {lockedTag}", new Point(w - 130, loopLaneTop), LoopBrush);
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
            return normalized * laneH;
        }

        private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default, 12, brush);
            context.DrawText(ft, origin);
        }
    }
}
