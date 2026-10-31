// Shared UI building blocks: animation helpers, the spring, buttons, waveform, dark menus.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Lumi
{
    // A damped spring, stepped every frame. Slightly under-damped so the island overshoots a touch.
    class Spring
    {
        public double Value, Target, Velocity;
        const double Stiffness = 300, Damping = 24;

        public Spring(double value) { Value = Target = value; }

        public bool Step(double dt)
        {
            double force = (Target - Value) * Stiffness - Velocity * Damping;
            Velocity += force * dt;
            Value += Velocity * dt;
            if (Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05)
            {
                Value = Target;
                Velocity = 0;
                return false;
            }
            return true;
        }
    }

    static class Ui
    {
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Symbols = new FontFamily("Segoe UI Symbol");
        public static readonly Color Green = C("#30D158"), Orange = C("#FF9F0A"), Red = C("#FF453A"),
            Gray = C("#8E8E93"), Dim = C("#636366"), Surface = C("#1FFFFFFF"), SurfaceHover = C("#33FFFFFF");
        static readonly Dictionary<UIElement, bool> shown = new Dictionary<UIElement, bool>();

        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static SolidColorBrush B(string hex) { return new SolidColorBrush(C(hex)); }
        public static SolidColorBrush B(Color c) { return new SolidColorBrush(c); }

        public static IEasingFunction EaseOut { get { return new CubicEase { EasingMode = EasingMode.EaseOut }; } }

        public static void Animate(IAnimatable target, DependencyProperty dp, double to, int ms)
        {
            target.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        public static void AnimateColor(IAnimatable target, DependencyProperty dp, Color to, int ms)
        {
            target.BeginAnimation(dp, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        public static void AnimateColor(SolidColorBrush brush, Color to, int ms)
        {
            AnimateColor(brush, SolidColorBrush.ColorProperty, to, ms);
        }

        // Content morphs in and out with a blur, like Apple's island. The blur effect is only
        // attached while it animates, since effects are expensive on a transparent window.
        public static void Reveal(FrameworkElement el, bool visible)
        {
            bool current;
            if (shown.TryGetValue(el, out current) && current == visible) return;
            shown[el] = visible;
            el.IsHitTestVisible = visible;

            var blur = new BlurEffect { Radius = visible ? 14 : 0 };
            el.Effect = blur;
            var blurAnim = new DoubleAnimation(visible ? 0 : 14, TimeSpan.FromMilliseconds(visible ? 340 : 160)) { EasingFunction = EaseOut };
            blurAnim.Completed += delegate { if (el.Effect == blur) el.Effect = null; };
            blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 300 : 140))
            {
                BeginTime = TimeSpan.FromMilliseconds(visible ? 70 : 0),
                EasingFunction = EaseOut
            });
        }

        public static void Hidden(FrameworkElement el)
        {
            el.Opacity = 0;
            el.IsHitTestVisible = false;
            shown[el] = false;
        }

        // Page content arrives element by element, 55 ms apart, rising slightly as it fades in.
        public static void Cascade(FrameworkElement page)
        {
            var panel = page as Panel;
            if (panel == null) return;
            int i = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child.Visibility != Visibility.Visible) continue;
                var begin = TimeSpan.FromMilliseconds(70 + 55 * i);
                child.Opacity = 0;
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = EaseOut });
                var move = child.RenderTransform as TranslateTransform;
                if (move == null && (child.RenderTransform == null || child.RenderTransform == Transform.Identity))
                {
                    move = new TranslateTransform();
                    child.RenderTransform = move;
                }
                if (move != null)
                {
                    move.Y = 8;
                    move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = EaseOut });
                }
                if (++i > 8) break;
            }
        }

        // Wraps a level bar so it can be clicked or dragged to set a value (0..1).
        public static Grid Slider(LevelBar bar, Action<double> onChange, Thickness margin)
        {
            var hit = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 14, Margin = margin };
            hit.Children.Add(bar.Root);
            Action<MouseEventArgs> update = e =>
            {
                double v = Math.Max(0, Math.Min(1, e.GetPosition(hit).X / Math.Max(1, hit.ActualWidth)));
                bar.Set(v, false);
                onChange(v);
            };
            hit.MouseLeftButtonDown += (s, e) => { e.Handled = true; hit.CaptureMouse(); update(e); };
            hit.MouseMove += (s, e) => { if (hit.IsMouseCaptured) update(e); };
            hit.MouseLeftButtonUp += (s, e) => { e.Handled = true; hit.ReleaseMouseCapture(); };
            return hit;
        }

        // Click = press and release over the same element. Handling the press keeps it
        // from reaching the island, which would otherwise start a window drag.
        public static void MakeClickable(FrameworkElement el, Action onClick, Action<bool> press)
        {
            el.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                el.CaptureMouse();
                if (press != null) press(true);
            };
            el.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!el.IsMouseCaptured) return;
                el.ReleaseMouseCapture();
                if (press != null) press(false);
                var p = e.GetPosition(el);
                if (p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight) onClick();
            };
        }

        // A rounded card that lightens on hover and dips when pressed.
        public static Border Card(UIElement content, double radius, Action onClick)
        {
            var bg = new SolidColorBrush(Surface);
            var scale = new ScaleTransform(1, 1);
            var card = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = bg,
                Child = content,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            if (onClick == null) return card;
            card.Cursor = Cursors.Hand;
            Action<bool> press = down =>
            {
                Animate(scale, ScaleTransform.ScaleXProperty, down ? 0.95 : 1, 140);
                Animate(scale, ScaleTransform.ScaleYProperty, down ? 0.95 : 1, 140);
            };
            card.MouseEnter += delegate { AnimateColor(bg, SurfaceHover, 150); };
            card.MouseLeave += delegate { AnimateColor(bg, Surface, 150); press(false); };
            MakeClickable(card, onClick, press);
            return card;
        }

        // Album art (or a fallback music glyph) clipped to a rounded square.
        public static Grid ArtBox(double size, double radius, out Image image)
        {
            var box = new Grid { Width = size, Height = size, Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius) };
            box.Children.Add(new Border
            {
                Background = new LinearGradientBrush(C("#1ED760"), C("#128C3E"), 45),
                Child = Glyph("", size * 0.42, Brushes.White)
            });
            image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            box.Children.Add(image);
            return box;
        }

        public static Border Badge(double size, double radius, out TextBlock glyph)
        {
            glyph = Glyph("", size * 0.42, Brushes.White);
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = glyph };
        }

        public static TextBlock Glyph(string glyph, double size, Brush color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = Icons,
                FontSize = size,
                Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static TextBlock Text(double size, Brush color, bool bold)
        {
            var t = new TextBlock
            {
                FontSize = size,
                Foreground = color,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        public static TextBlock Text(string text, double size, Brush color, bool bold)
        {
            var t = Text(size, color, bold);
            t.Text = text;
            return t;
        }

        public static readonly GridLength Auto = GridLength.Auto, Star = new GridLength(1, GridUnitType.Star);

        public static Grid Columns(params GridLength[] columns)
        {
            var g = new Grid();
            foreach (var c in columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        public static Grid Rows(int count)
        {
            var g = new Grid();
            for (int i = 0; i < count; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return g;
        }

        public static T At<T>(Grid g, T el, int col, int row) where T : UIElement
        {
            Grid.SetColumn(el, col);
            Grid.SetRow(el, row);
            g.Children.Add(el);
            return el;
        }

        public static void Open(string target, string fallback)
        {
            try { Process.Start(target); }
            catch
            {
                if (fallback == null) return;
                try { Process.Start(fallback); } catch { }
            }
        }
    }

    // Pill button: optional icon glyph and/or text, with hover, press and selected states.
    class Chip
    {
        static readonly Color Fg = Ui.C("#AEAEB2");

        public readonly Border Root;
        readonly TextBlock glyphBlock;
        readonly SolidColorBrush bgBrush, fgBrush = new SolidColorBrush(Fg);
        readonly ScaleTransform scale = new ScaleTransform(1, 1);
        readonly Color normalBg, selectedBg, selectedFg;
        bool selected, hover;

        public Chip(string glyph, string text, double height, double fontSize, Color selectedBg, Color selectedFg, Action onClick)
            : this(glyph, text, height, fontSize, Ui.Surface, selectedBg, selectedFg, onClick) { }

        public Chip(string glyph, string text, double height, double fontSize, Color normalBg, Color selectedBg, Color selectedFg, Action onClick)
        {
            this.normalBg = normalBg;
            this.selectedBg = selectedBg;
            this.selectedFg = selectedFg;
            bgBrush = new SolidColorBrush(normalBg);
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (glyph != null)
            {
                glyphBlock = Ui.Glyph(glyph, fontSize, fgBrush);
                glyphBlock.Margin = new Thickness(0, 0, text != null ? 7 : 0, 0);
                content.Children.Add(glyphBlock);
            }
            if (text != null) content.Children.Add(Ui.Text(text, fontSize, fgBrush, true));
            Root = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(height / 2),
                Padding = new Thickness(12, 0, 12, 0),
                Background = bgBrush,
                Child = content,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            Root.MouseEnter += delegate { hover = true; Paint(); };
            Root.MouseLeave += delegate { hover = false; Paint(); Press(false); };
            Ui.MakeClickable(Root, onClick, Press);
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Paint(); }
        }

        public void SetGlyph(string glyph) { if (glyphBlock != null) glyphBlock.Text = glyph; }

        void Press(bool down)
        {
            Ui.Animate(scale, ScaleTransform.ScaleXProperty, down ? 0.9 : 1, 140);
            Ui.Animate(scale, ScaleTransform.ScaleYProperty, down ? 0.9 : 1, 140);
        }

        void Paint()
        {
            Ui.AnimateColor(bgBrush, selected ? selectedBg : hover ? Ui.SurfaceHover : normalBg, 160);
            Ui.AnimateColor(fgBrush, selected ? selectedFg : hover ? Colors.White : Fg, 160);
        }
    }

    // Audio waveform bars, driven every frame from the real output level.
    class Equalizer
    {
        static readonly double[] Freq = { 7.3, 11.1, 8.6, 13.4, 9.7 };
        public readonly StackPanel Root;
        readonly ScaleTransform[] bars;
        readonly double[] values;
        public bool Active;

        public Equalizer(Brush brush, int count, double barHeight, double barWidth)
        {
            bars = new ScaleTransform[count];
            values = new double[count];
            Root = new StackPanel { Orientation = Orientation.Horizontal, Height = barHeight, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < count; i++)
            {
                values[i] = 0.18;
                bars[i] = new ScaleTransform(1, 0.18);
                Root.Children.Add(new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    RadiusX = barWidth / 2,
                    RadiusY = barWidth / 2,
                    Fill = brush,
                    Margin = new Thickness(barWidth * 0.45, 0, barWidth * 0.45, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = bars[i]
                });
            }
        }

        // Returns true while bars are still moving.
        public bool Tick(double level, double t)
        {
            bool moving = false;
            for (int i = 0; i < bars.Length; i++)
            {
                double target = Active
                    ? 0.2 + 0.8 * level * (0.4 + 0.6 * Math.Abs(Math.Sin(t * Freq[i % Freq.Length] + i * 1.7)))
                    : 0.18;
                values[i] += (target - values[i]) * 0.35;
                if (Math.Abs(target - values[i]) > 0.005) moving = true;
                bars[i].ScaleY = Math.Max(0.12, Math.Min(1, values[i]));
            }
            return moving;
        }
    }

    // A thin rounded level bar (progress, volume, CPU...).
    class LevelBar
    {
        public readonly Grid Root;
        public readonly SolidColorBrush Fill;
        readonly ScaleTransform scale = new ScaleTransform(0, 1);

        public LevelBar(double height, Color color)
        {
            Fill = new SolidColorBrush(color);
            Root = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
            Root.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Ui.B("#33FFFFFF") });
            Root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(height / 2),
                Background = Fill,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = scale
            });
        }

        public void Set(double value, bool animate)
        {
            value = Math.Max(0, Math.Min(1, value));
            if (animate) Ui.Animate(scale, ScaleTransform.ScaleXProperty, value, 250);
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.ScaleX = value;
            }
        }
    }

    // Dark theme for the tray / right-click menu.
    class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Drawing.Color.White : Drawing.Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Drawing.Color.White;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(48, 209, 88), 2))
            {
                e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Drawing.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                    new Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                    new Drawing.PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
                });
            }
        }
    }

    class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        static readonly Drawing.Color Bg = Drawing.Color.FromArgb(30, 30, 32), Hover = Drawing.Color.FromArgb(58, 58, 62),
            Line = Drawing.Color.FromArgb(56, 56, 60);
        public override Drawing.Color ToolStripDropDownBackground { get { return Bg; } }
        public override Drawing.Color ImageMarginGradientBegin { get { return Bg; } }
        public override Drawing.Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Drawing.Color ImageMarginGradientEnd { get { return Bg; } }
        public override Drawing.Color MenuBorder { get { return Line; } }
        public override Drawing.Color MenuItemBorder { get { return Hover; } }
        public override Drawing.Color MenuItemSelected { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Drawing.Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Drawing.Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Drawing.Color SeparatorDark { get { return Line; } }
        public override Drawing.Color SeparatorLight { get { return Bg; } }
        public override Drawing.Color CheckBackground { get { return Bg; } }
        public override Drawing.Color CheckSelectedBackground { get { return Hover; } }
        public override Drawing.Color CheckPressedBackground { get { return Hover; } }
    }
}
