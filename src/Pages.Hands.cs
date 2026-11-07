// The Hand Control app: turn gesture control on or off, see the hand it's following, and which
// gesture does what. The camera work lives in Hands.cs.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Lumi
{
    partial class IslandWindow
    {
        static readonly Color HandBlue = Ui.C("#0A84FF");
        static readonly int[][] HandBones =
        {
            new[] { 0, 1, 2, 3, 4 }, new[] { 0, 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 },
            new[] { 13, 14, 15, 16 }, new[] { 0, 17, 18, 19, 20 }, new[] { 5, 9, 13, 17 }
        };

        readonly HandControl hands = new HandControl();
        bool handsOn;   // remembered, so it comes back on when Lumi starts
        Border handsBadge;
        DropShadowEffect handsGlow;
        SolidColorBrush handsTrack;
        TranslateTransform handsKnob;
        TextBlock handsSubtitle, handsGestureText, handsIdleText;
        Canvas handsCanvas;
        Polyline[] handsLines;
        Ellipse[] handsDots;
        Border[] handsRows;
        Chip handsSettingsChip;
        const double HandsViewH = 132;

        FrameworkElement BuildHandsPage()
        {
            var g = Ui.Rows(4);

            // Header: icon, name and status, on/off switch (same as Always On).
            var header = Ui.At(g, Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto), 0, 0);
            handsGlow = new DropShadowEffect { Color = HandBlue, BlurRadius = 20, ShadowDepth = 0, Opacity = 0 };
            TextBlock glyph;
            handsBadge = Ui.At(header, Ui.Badge(44, 22, out glyph), 0, 0);
            glyph.Text = "";
            handsBadge.Effect = handsGlow;
            var text = Ui.At(header, new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            text.Children.Add(Ui.Text("Hand Control", 15, Brushes.White, true));
            handsSubtitle = Ui.Text(12, Ui.B(Ui.Gray), false);
            handsSubtitle.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(handsSubtitle);

            handsTrack = new SolidColorBrush(Track);
            handsKnob = new TranslateTransform(0, 0);
            var toggle = Ui.At(header, new Border
            {
                Width = 48,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = handsTrack,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(3, 0, 0, 0),
                    RenderTransform = handsKnob,
                    Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3 }
                }
            }, 2, 0);
            Ui.MakeClickable(toggle, delegate { SetHands(!handsOn); }, null);

            // The hand it sees, drawn as a skeleton (never the camera picture), mirrored like a selfie.
            handsCanvas = new Canvas { ClipToBounds = true };
            handsLines = new Polyline[HandBones.Length];
            for (int i = 0; i < HandBones.Length; i++)
            {
                handsLines[i] = new Polyline { Stroke = Ui.B(HandBlue), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
                handsCanvas.Children.Add(handsLines[i]);
            }
            handsDots = new Ellipse[21];
            for (int i = 0; i < 21; i++)
            {
                handsDots[i] = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White };
                handsCanvas.Children.Add(handsDots[i]);
            }
            var view = new Grid();
            view.Children.Add(handsCanvas);
            handsIdleText = Ui.Text("Camera off", 12, Ui.B(Ui.Dim), false);
            handsIdleText.HorizontalAlignment = HorizontalAlignment.Center;
            handsIdleText.TextAlignment = TextAlignment.Center;
            handsIdleText.TextWrapping = TextWrapping.Wrap;
            handsIdleText.Margin = new Thickness(24, 0, 24, 0);
            view.Children.Add(handsIdleText);
            handsGestureText = Ui.Text(12, Brushes.White, true);
            handsGestureText.HorizontalAlignment = HorizontalAlignment.Center;
            handsGestureText.VerticalAlignment = VerticalAlignment.Bottom;
            handsGestureText.Margin = new Thickness(0, 0, 0, 8);
            view.Children.Add(handsGestureText);
            var viewBox = Ui.At(g, new Border
            {
                Height = HandsViewH,
                CornerRadius = new CornerRadius(16),
                Background = Ui.B("#0FFFFFFF"),
                Margin = new Thickness(0, 14, 0, 0),
                Child = view
            }, 0, 1);

            // What each gesture does; the row lights up while you hold it.
            var legend = Ui.At(g, new UniformGrid { Columns = 2, Margin = new Thickness(-3, 10, -3, 0) }, 0, 2);
            var rows = new[]
            {
                new[] { "", "Thumbs up", "Volume up" },
                new[] { "", "Thumbs down", "Volume down" },
                new[] { "", "Point right", "Right arrow" },
                new[] { "", "Point left", "Left arrow" },
                new[] { "", "Pinch", "Space" }
            };
            handsRows = new Border[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var row = Ui.Columns(Ui.Auto, Ui.Star);
                var icon = Ui.At(row, Ui.Glyph(rows[i][0], 15, Brushes.White), 0, 0);
                icon.Width = 22;
                icon.Margin = new Thickness(0, 0, 8, 0);
                var words = Ui.At(row, new StackPanel(), 1, 0);
                words.Children.Add(Ui.Text(rows[i][1], 11.5, Brushes.White, true));
                words.Children.Add(Ui.Text(rows[i][2], 10.5, Ui.B(Ui.Gray), false));
                handsRows[i] = new Border
                {
                    Child = row,
                    CornerRadius = new CornerRadius(12),
                    Background = Ui.B(Ui.Surface),
                    Padding = new Thickness(10, 6, 8, 6),
                    Margin = new Thickness(3)
                };
                legend.Children.Add(handsRows[i]);
            }

            var bottom = Ui.At(g, Ui.Columns(Ui.Star, Ui.Auto), 0, 3);
            bottom.Margin = new Thickness(0, 8, 0, 0);
            var note = Ui.At(bottom, Ui.Text("Hold your whole hand up in view for a moment. Keys go to the window you're using.", 10.5, Ui.B(Ui.Dim), false), 0, 0);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(2, 0, 8, 0);
            handsSettingsChip = new Chip(null, "Camera settings", 26, 11.5, Ui.C("#163A22"), Ui.Green, delegate { Ui.Open("ms-settings:privacy-webcam", null); });
            handsSettingsChip.Root.Visibility = Visibility.Collapsed;
            Ui.At(bottom, handsSettingsChip.Root, 1, 0);

            hands.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateHands));
            hands.FrameRead += f => Dispatcher.BeginInvoke(new Action(() => OnHandFrame(f)));
            hands.Fired += gesture => Dispatcher.BeginInvoke(new Action(() => OnHandFired(gesture)));
            UpdateHands();
            return g;
        }

        void SetHands(bool on)
        {
            handsOn = on;
            if (on) hands.Start();
            else hands.Stop();
            UpdateHands();
            SaveSettings();
        }

        void UpdateHands()
        {
            // It stopped by itself (camera busy, no access...): show the switch as off.
            bool on = hands.Running;
            if (handsOn && !on && hands.Problem) handsOn = false;
            Ui.AnimateColor(handsTrack, on ? HandBlue : Track, 220);
            handsKnob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 20 : 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            });
            handsBadge.Background = on
                ? new LinearGradientBrush(Ui.C("#409CFF"), Ui.C("#0060DF"), 90)
                : new LinearGradientBrush(Ui.C("#48484A"), Ui.C("#2C2C2E"), 90);
            handsGlow.Opacity = on ? 0.6 : 0;
            handsSubtitle.Text = on ? hands.Status : hands.Problem ? "Stopped" : "Off · control your PC with gestures";
            handsIdleText.Text = hands.Problem ? hands.Status : on ? (hands.Status == "Watching your hand" ? "" : hands.Status) : "Camera off";
            handsIdleText.Foreground = Ui.B(hands.Problem ? Ui.Orange : Ui.Dim);
            handsSettingsChip.Root.Visibility = hands.Problem && hands.Status.Contains("Settings") ? Visibility.Visible : Visibility.Collapsed;
            if (!on) ClearHand();
            RefitPage(TabHands);
        }

        void ClearHand()
        {
            foreach (var line in handsLines) line.Points = new PointCollection();
            foreach (var dot in handsDots) dot.Visibility = Visibility.Collapsed;
            handsGestureText.Text = "";
            HighlightGesture(Gesture.None);
        }

        void OnHandFrame(HandFrame f)
        {
            if (f.Points == null)
            {
                ClearHand();
                if (hands.Running) handsIdleText.Text = hands.Status == "Watching your hand" ? "" : hands.Status;
                return;
            }
            handsIdleText.Text = "";
            handsGestureText.Text = GestureReader.Name(f.Gesture);
            HighlightGesture(f.Gesture);
            if (!handsCanvas.IsVisible || handsCanvas.ActualWidth < 1) return;

            // Fit the hand into the view, mirrored so moving right moves it right.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in f.Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            double w = handsCanvas.ActualWidth, h = handsCanvas.ActualHeight - 22;
            double scale = Math.Min((w - 40) / Math.Max(1, maxX - minX), (h - 16) / Math.Max(1, maxY - minY));
            scale = Math.Min(scale, 3);
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            Func<HandPoint, Point> map = p => new Point(w / 2 - (p.X - cx) * scale, h / 2 + 4 + (p.Y - cy) * scale);
            for (int i = 0; i < HandBones.Length; i++)
            {
                var points = new PointCollection();
                foreach (int j in HandBones[i]) points.Add(map(f.Points[j]));
                handsLines[i].Points = points;
            }
            for (int i = 0; i < 21; i++)
            {
                var q = map(f.Points[i]);
                Canvas.SetLeft(handsDots[i], q.X - 3);
                Canvas.SetTop(handsDots[i], q.Y - 3);
                handsDots[i].Visibility = Visibility.Visible;
            }
        }

        void HighlightGesture(Gesture g)
        {
            int active = GestureRow(g);
            for (int i = 0; i < handsRows.Length; i++)
            {
                var color = i == active ? Ui.C("#330A84FF") : Ui.Surface;
                var brush = handsRows[i].Background as SolidColorBrush;
                if (brush == null || brush.Color != color) handsRows[i].Background = new SolidColorBrush(color);
            }
        }

        // A short flash on the gesture's row when it fires.
        void OnHandFired(Gesture g)
        {
            int row = GestureRow(g);
            if (row < 0) return;
            var brush = new SolidColorBrush(HandBlue);
            handsRows[row].Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Ui.C("#330A84FF"), TimeSpan.FromMilliseconds(450)));
        }

        static int GestureRow(Gesture g)
        {
            switch (g)
            {
                case Gesture.ThumbsUp: return 0;
                case Gesture.ThumbsDown: return 1;
                case Gesture.PointRight: return 2;
                case Gesture.PointLeft: return 3;
                case Gesture.Pinch: return 4;
                default: return -1;
            }
        }
    }
}
