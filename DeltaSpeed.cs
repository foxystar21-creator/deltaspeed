// ============================================================
//  DELTA SPEED — Custom ATAS Indicator
//  Compares real-time delta accumulation with the previous bar's
//  ghost replay, driven by elapsed wall-clock time.
//
//  Build requirements:
//    • .NET 8 Class Library
//    • Reference ATAS.Indicators.dll  (from ATAS install folder)
//    • Reference Utils.Common.dll     (same folder, for logging)
//    • Set <UseWPF>true</UseWPF> in .csproj
//
//  Deploy:
//    Copy DeltaSpeed.dll → %APPDATA%\ATAS\Indicators\
//    Click the reload-button inside ATAS.
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using Utils.Common.Logging;

namespace DeltaSpeedIndicator
{
    // ── Data model ────────────────────────────────────────────────────────────
    // Stores the full intrabar delta progression for one completed bar.

    /// <summary>
    /// Holds the time-stamped delta snapshot list for a single bar.
    /// Each element represents the cumulative delta at a specific tick moment.
    /// </summary>
    internal sealed class DeltaBarData
    {
        /// <summary>Cumulative delta values sampled at each tick.</summary>
        public List<decimal> DeltaProgression { get; } = new();

        /// <summary>Time of the bar's first tick (bar open).</summary>
        public DateTime StartTime { get; set; }

        /// <summary>Time of the bar's last tick (bar close).</summary>
        public DateTime EndTime { get; set; }

        /// <summary>Elapsed duration of this bar.</summary>
        public TimeSpan Duration => EndTime > StartTime
            ? EndTime - StartTime
            : TimeSpan.Zero;

        /// <summary>Final delta of the bar (last snapshot).</summary>
        public decimal FinalDelta => DeltaProgression.Count > 0
            ? DeltaProgression[^1]
            : 0m;

        /// <summary>Maximum cumulative delta reached during the bar.</summary>
        public decimal MaxDelta { get; set; }

        /// <summary>Minimum cumulative delta reached during the bar.</summary>
        public decimal MinDelta { get; set; }

        /// <summary>Returns the ghost (partial) delta up to <paramref name="progress"/> [0..1].</summary>
        public decimal GetGhostDelta(double progress)
        {
            if (DeltaProgression.Count == 0) return 0m;
            if (DeltaProgression.Count == 1) return DeltaProgression[0];

            int idx = (int)(progress * (DeltaProgression.Count - 1));
            idx = Math.Max(0, Math.Min(idx, DeltaProgression.Count - 1));
            return DeltaProgression[idx];
        }

        /// <summary>Returns the max delta seen up to <paramref name="progress"/>.</summary>
        public decimal GetGhostMax(double progress)
        {
            if (DeltaProgression.Count == 0) return 0m;
            int endIdx = (int)(progress * (DeltaProgression.Count - 1));
            endIdx = Math.Max(0, Math.Min(endIdx, DeltaProgression.Count - 1));
            decimal max = 0m;
            for (int i = 0; i <= endIdx; i++)
                max = Math.Max(max, DeltaProgression[i]);
            return max;
        }

        /// <summary>Returns the min delta seen up to <paramref name="progress"/>.</summary>
        public decimal GetGhostMin(double progress)
        {
            if (DeltaProgression.Count == 0) return 0m;
            int endIdx = (int)(progress * (DeltaProgression.Count - 1));
            endIdx = Math.Max(0, Math.Min(endIdx, DeltaProgression.Count - 1));
            decimal min = 0m;
            for (int i = 0; i <= endIdx; i++)
                min = Math.Min(min, DeltaProgression[i]);
            return min;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  INDICATOR CLASS
    // ══════════════════════════════════════════════════════════════════════════

    [DisplayName("Delta Speed")]
    [Display(Description =
        "Displays current bar delta and ghost replay of the previous bar's " +
        "delta progression, driven by elapsed wall-clock time.")]
    public sealed class DeltaSpeed : Indicator
    {
        // ── Settings ─────────────────────────────────────────────────────────

        private bool _showGhost = true;
        private int _maxStoredBars = 300;
        private System.Windows.Media.Color _upColor   = System.Windows.Media.Colors.LimeGreen;
        private System.Windows.Media.Color _downColor = System.Windows.Media.Colors.OrangeRed;
        private System.Windows.Media.Color _ghostColor = System.Windows.Media.Colors.Gray;

        [Display(Name = "Show Ghost", GroupName = "Settings", Order = 1)]
        public bool ShowGhost
        {
            get => _showGhost;
            set { _showGhost = value; RedrawChart(); }
        }

        [Display(Name = "Max Stored Bars", GroupName = "Settings", Order = 2)]
        public int MaxStoredBars
        {
            get => _maxStoredBars;
            set
            {
                _maxStoredBars = Math.Max(10, value);
                TrimHistory();
            }
        }

        [Display(Name = "Up Color", GroupName = "Colors", Order = 10)]
        public System.Windows.Media.Color UpColor
        {
            get => _upColor;
            set { _upColor = value; RedrawChart(); }
        }

        [Display(Name = "Down Color", GroupName = "Colors", Order = 11)]
        public System.Windows.Media.Color DownColor
        {
            get => _downColor;
            set { _downColor = value; RedrawChart(); }
        }

        [Display(Name = "Ghost Color", GroupName = "Colors", Order = 12)]
        public System.Windows.Media.Color GhostColor
        {
            get => _ghostColor;
            set { _ghostColor = value; RedrawChart(); }
        }

        // ── State ─────────────────────────────────────────────────────────────

        // Maps bar-index → historical delta data (ring-buffer keyed by bar number)
        private readonly Dictionary<int, DeltaBarData> _barsHistory = new();

        // Current bar live tracking
        private decimal _currentBarDelta;           // live delta accumulation
        private decimal _currentBarMaxDelta;
        private decimal _currentBarMinDelta;
        private DateTime _currentBarStartTime;
        private DeltaBarData _currentBarBuilder;    // building progression list in real-time

        // Ghost tracking for current (last) bar
        private double _ghostProgress;              // 0..1

        // Which timeframe string was active last time we initialized
        private string _lastTimeframe = string.Empty;

        // ── DataSeries (hidden – we draw everything in OnRender) ──────────────

        // We need at least one DataSeries to keep ATAS happy.
        // Repurpose it as the zero-line anchor.
        private readonly ValueDataSeries _zeroLine = new("_zero")
        {
            IsHidden = true,
            ShowZeroValue = false,
            Color = System.Windows.Media.Colors.Transparent.Convert()
        };

        // ── Constructor ───────────────────────────────────────────────────────

        public DeltaSpeed() : base(true)
        {
            // Separate panel below price
            Panel = IndicatorDataProvider.NewPanel;
            DenyToChangePanel = false;

            // Enable custom drawing
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            // Replace default DataSeries[0] with our hidden placeholder
            DataSeries[0] = _zeroLine;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void OnInitialize()
        {
            this.LogInfo("[DeltaSpeed] Initialized.");

            // If timeframe changed, wipe history so we start clean
            string tf = ChartInfo?.TimeFrame ?? string.Empty;
            if (tf != _lastTimeframe)
            {
                _barsHistory.Clear();
                _lastTimeframe = tf;
                this.LogInfo($"[DeltaSpeed] Timeframe changed to '{tf}', history cleared.");
            }
        }

        // ── OnCalculate: called on every historical bar, then every tick ───────

        protected override void OnCalculate(int bar, decimal value)
        {
            var candle = GetCandle(bar);
            bool isCurrentBar = bar == CurrentBar - 1;

            if (isCurrentBar)
            {
                // ── Live bar ──────────────────────────────────────────────────
                OnLiveBarTick(bar, candle);
            }
            else
            {
                // ── Historical bar ────────────────────────────────────────────
                OnHistoricalBar(bar, candle);
            }

            // Always write 0 to the zero-line series so panel scales work
            _zeroLine[bar] = 0m;
        }

        // ── Historical bar processing ─────────────────────────────────────────

        private void OnHistoricalBar(int bar, IndicatorCandle candle)
        {
            if (_barsHistory.ContainsKey(bar))
                return; // already processed

            // We do NOT have individual tick history for historical bars,
            // so we approximate using a linear interpolation from 0 → finalDelta.
            // This matches "If tick history NOT available → linear interpolation" requirement.

            var data = new DeltaBarData
            {
                StartTime = candle.Time,
                EndTime   = candle.Time + TimeSpan.FromSeconds(1) // approximate end
            };

            decimal finalDelta = candle.Delta;
            decimal maxDelta   = candle.MaxDelta;  // ATAS candle max delta field
            decimal minDelta   = candle.MinDelta;  // ATAS candle min delta field

            // Build a linear approximation progression (20 steps for smoothness)
            const int Steps = 20;
            for (int s = 0; s <= Steps; s++)
            {
                double t   = (double)s / Steps;
                decimal pt = (decimal)t * finalDelta;
                data.DeltaProgression.Add(pt);
            }

            data.MaxDelta = maxDelta;
            data.MinDelta = minDelta;

            StoreBarData(bar, data);
        }

        // ── Live bar tick processing ──────────────────────────────────────────

        private void OnLiveBarTick(int bar, IndicatorCandle candle)
        {
            bool isNewBar = (_currentBarBuilder == null ||
                             (candle.Time.Date != _currentBarStartTime.Date &&
                              candle.Time < _currentBarStartTime + TimeSpan.FromSeconds(1)));

            // Detect bar change: if the candle's time moved past our tracked open
            // or this is the very first tick we see.
            if (_currentBarBuilder == null)
            {
                StartNewBar(bar, candle);
            }

            // The candle.Delta from ATAS is the real-time running delta for the live bar.
            _currentBarDelta    = candle.Delta;
            _currentBarMaxDelta = Math.Max(candle.MaxDelta, _currentBarDelta);
            _currentBarMinDelta = Math.Min(candle.MinDelta, _currentBarDelta);

            // Record snapshot for live progression
            _currentBarBuilder.DeltaProgression.Add(_currentBarDelta);
            _currentBarBuilder.MaxDelta = _currentBarMaxDelta;
            _currentBarBuilder.MinDelta = _currentBarMinDelta;
            _currentBarBuilder.EndTime  = candle.LastTime != default
                ? candle.LastTime
                : DateTime.UtcNow;

            // ── Time-based ghost progress ─────────────────────────────────────
            // Ghost progress is based on elapsed time, NOT tick count.
            if (_barsHistory.TryGetValue(bar - 1, out var prevData) &&
                prevData.Duration.TotalMilliseconds > 0)
            {
                DateTime now = _currentBarBuilder.EndTime;
                TimeSpan elapsed = now - _currentBarStartTime;

                _ghostProgress = elapsed.TotalMilliseconds / prevData.Duration.TotalMilliseconds;
                _ghostProgress = Math.Max(0.0, Math.Min(1.0, _ghostProgress));
            }
            else
            {
                _ghostProgress = 0.0;
            }
        }

        // Called when a genuine new bar starts (bar number incremented by ATAS)
        private void StartNewBar(int bar, IndicatorCandle candle)
        {
            // Commit the previous builder (if any) to history
            if (_currentBarBuilder != null && _currentBarBuilder.DeltaProgression.Count > 0)
            {
                // bar-1 is the bar we just finished
                StoreBarData(bar - 1, _currentBarBuilder);
            }

            // Re-initialize for new bar
            _currentBarStartTime = candle.Time;
            _currentBarDelta     = 0m;
            _currentBarMaxDelta  = 0m;
            _currentBarMinDelta  = 0m;
            _ghostProgress       = 0.0;

            _currentBarBuilder = new DeltaBarData
            {
                StartTime = _currentBarStartTime
            };
        }

        // ── Storage helpers ───────────────────────────────────────────────────

        private void StoreBarData(int bar, DeltaBarData data)
        {
            _barsHistory[bar] = data;
            TrimHistory();
        }

        private void TrimHistory()
        {
            // Keep only the last N bars to limit memory usage
            while (_barsHistory.Count > _maxStoredBars)
            {
                // Remove the oldest bar key
                int minKey = int.MaxValue;
                foreach (var k in _barsHistory.Keys)
                    if (k < minKey) minKey = k;
                _barsHistory.Remove(minKey);
            }
        }

        // ── Custom rendering ──────────────────────────────────────────────────

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null) return;

            // Determine the value scale — find the largest absolute delta visible
            decimal maxAbs = FindMaxAbsDeltaVisible();
            if (maxAbs == 0m) maxAbs = 1m; // guard against division by zero

            int panelHeight  = ChartInfo.Region.Height;
            int panelWidth   = ChartInfo.Region.Width;
            int centerY      = panelHeight / 2;

            // Derived rendering colors (System.Drawing)
            var upColorDraw    = UpColor.Convert();
            var downColorDraw  = DownColor.Convert();
            var ghostColorDraw = GhostColor.Convert();
            var ghostFillDraw  = Color.FromArgb(120, ghostColorDraw); // semi-transparent fill

            // Draw zero line
            using var zeroPen = new RenderPen(Color.FromArgb(60, 200, 200, 200));
            context.DrawLine(zeroPen, 0, centerY, panelWidth, centerY);

            // Draw label
            var labelFont  = new RenderFont("Consolas", 9f);
            context.DrawString("Delta Speed", labelFont, Color.FromArgb(100, 180, 180, 180),
                new Rectangle(4, 2, 200, 16));

            // Iterate visible bars
            for (int bar = FirstVisibleBarNumber; bar <= LastVisibleBarNumber; bar++)
            {
                bool isLiveBar = (bar == CurrentBar - 1);

                // ── Get current-bar delta values ──────────────────────────────
                decimal barDelta, barMax, barMin;

                if (isLiveBar)
                {
                    barDelta = _currentBarDelta;
                    barMax   = _currentBarMaxDelta;
                    barMin   = _currentBarMinDelta;
                }
                else if (_barsHistory.TryGetValue(bar, out var hData))
                {
                    barDelta = hData.FinalDelta;
                    barMax   = hData.MaxDelta;
                    barMin   = hData.MinDelta;
                }
                else
                {
                    var c  = GetCandle(bar);
                    barDelta = c.Delta;
                    barMax   = c.MaxDelta;
                    barMin   = c.MinDelta;
                }

                // ── Ghost delta values ────────────────────────────────────────
                decimal ghostDelta = 0m;
                decimal ghostMax   = 0m;
                decimal ghostMin   = 0m;

                if (_showGhost && _barsHistory.TryGetValue(bar - 1, out var prevBarData))
                {
                    double progress = isLiveBar ? _ghostProgress : 1.0;
                    ghostDelta = prevBarData.GetGhostDelta(progress);
                    ghostMax   = prevBarData.GetGhostMax(progress);
                    ghostMin   = prevBarData.GetGhostMin(progress);
                }

                // ── Pixel geometry ────────────────────────────────────────────
                int xBar  = ChartInfo.GetXByBar(bar);
                int bw    = Math.Max(2, (int)ChartInfo.PriceChartContainer.BarsWidth);
                int halfW = Math.Max(1, bw / 2 - 1);

                // Current candle — centered on the bar
                DrawDeltaCandle(context, xBar, centerY, panelHeight,
                    barDelta, barMax, barMin, maxAbs, bw,
                    upColorDraw, downColorDraw,
                    offset: 0);

                // Ghost candle — shifted slightly LEFT of the current candle
                if (_showGhost && _barsHistory.ContainsKey(bar - 1))
                {
                    int ghostOffset = -(halfW + 3);
                    DrawDeltaCandle(context, xBar, centerY, panelHeight,
                        ghostDelta, ghostMax, ghostMin, maxAbs, halfW,
                        ghostFillDraw, ghostFillDraw,
                        offset: ghostOffset,
                        borderColor: ghostColorDraw);
                }
            }
        }

        // ── DrawDeltaCandle helper ────────────────────────────────────────────
        // Draws a single open=0 delta candle at pixel position (x, centerY).
        // offset shifts the candle left/right relative to bar center.

        private static void DrawDeltaCandle(
            RenderContext context,
            int xBar, int centerY, int panelHeight,
            decimal delta, decimal maxDelta, decimal minDelta,
            decimal maxAbs,
            int width,
            Color bodyFill, Color bodyFillNeg,
            int offset = 0,
            Color? borderColor = null)
        {
            if (width < 1) width = 1;

            // Scale: map delta → pixels. Half of panel height available per side.
            double scale   = (panelHeight / 2.0 - 4) / (double)maxAbs;
            int closeY     = centerY - (int)((double)delta    * scale);
            int highY      = centerY - (int)((double)maxDelta * scale);
            int lowY       = centerY - (int)((double)minDelta * scale);

            int bodyTop    = Math.Min(centerY, closeY);  // open=0 → centerY
            int bodyBottom = Math.Max(centerY, closeY);
            int bodyH      = Math.Max(1, bodyBottom - bodyTop);

            int x = xBar + offset;

            // Wick (high/low shadows)
            var wickColor = borderColor ?? (delta >= 0 ? bodyFill : bodyFillNeg);
            using var wickPen = new RenderPen(Color.FromArgb(150, wickColor));
            context.DrawLine(wickPen, x + width / 2, highY, x + width / 2, lowY);

            // Body fill
            var fillColor = delta >= 0 ? bodyFill : bodyFillNeg;
            var bodyRect  = new Rectangle(x, bodyTop, width, bodyH);
            context.FillRectangle(fillColor, bodyRect);

            // Body border
            if (borderColor.HasValue)
            {
                using var pen = new RenderPen(borderColor.Value);
                context.DrawRectangle(pen, bodyRect);
            }
        }

        // ── Scale helper ──────────────────────────────────────────────────────

        private decimal FindMaxAbsDeltaVisible()
        {
            decimal maxAbs = 1m;

            for (int bar = FirstVisibleBarNumber; bar <= LastVisibleBarNumber; bar++)
            {
                decimal d = 0m, hi = 0m, lo = 0m;

                if (bar == CurrentBar - 1)
                {
                    d  = Math.Abs(_currentBarDelta);
                    hi = Math.Abs(_currentBarMaxDelta);
                    lo = Math.Abs(_currentBarMinDelta);
                }
                else if (_barsHistory.TryGetValue(bar, out var hData))
                {
                    d  = Math.Abs(hData.FinalDelta);
                    hi = Math.Abs(hData.MaxDelta);
                    lo = Math.Abs(hData.MinDelta);
                }
                else
                {
                    var c = GetCandle(bar);
                    d  = Math.Abs(c.Delta);
                    hi = Math.Abs(c.MaxDelta);
                    lo = Math.Abs(c.MinDelta);
                }

                maxAbs = Math.Max(maxAbs, Math.Max(d, Math.Max(hi, lo)));

                // Also include ghost deltas
                if (_showGhost && _barsHistory.TryGetValue(bar - 1, out var prev))
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(prev.MaxDelta));
                    maxAbs = Math.Max(maxAbs, Math.Abs(prev.MinDelta));
                }
            }

            return maxAbs;
        }

        // ── OnNewBar: called by ATAS when a new bar opens ─────────────────────
        //    (Override if the base class exposes it; otherwise StartNewBar is
        //     triggered from OnCalculate on the first tick of a new bar.)

        protected override void OnNewBar(int bar)
        {
            // Commit builder from the bar that just closed (bar - 1)
            if (_currentBarBuilder != null && _currentBarBuilder.DeltaProgression.Count > 0)
            {
                _currentBarBuilder.EndTime = DateTime.UtcNow;
                StoreBarData(bar - 1, _currentBarBuilder);
            }

            // Prepare fresh builder for the new bar
            var candle = GetCandle(bar);
            _currentBarStartTime = candle.Time;
            _currentBarDelta     = 0m;
            _currentBarMaxDelta  = 0m;
            _currentBarMinDelta  = 0m;
            _ghostProgress       = 0.0;

            _currentBarBuilder = new DeltaBarData
            {
                StartTime = _currentBarStartTime
            };
        }
    }
}
