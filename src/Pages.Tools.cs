// Productivity apps in the island: clipboard history, notes, colour picker, focus timer, todos,
// Bluetooth devices, local dev servers and a teleprompter. Plus the text box used for typing.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Lumi
{
    partial class IslandWindow
    {
        // ---------- Typing ----------

        // A dark text box. The island normally never takes focus; clicking a box lets it take
        // the keyboard until you click away or press Escape.
        TextBox Input(string placeholder, bool multiline, out Border host)
        {
            var box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                SelectionBrush = Ui.B("#0A84FF"),
                FontSize = 13,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0)
            };
            var hint = Ui.Text(placeholder, 13, Ui.B(Ui.Dim), false);
            hint.IsHitTestVisible = false;
            hint.Margin = new Thickness(3, 0, 0, 0);
            if (multiline) hint.VerticalAlignment = VerticalAlignment.Top;
            box.TextChanged += delegate { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            box.PreviewMouseLeftButtonDown += delegate { BeginTyping(); };
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                EndTyping();
                SetView(ViewCompact);
            };
            var grid = new Grid();
            grid.Children.Add(hint);
            grid.Children.Add(box);
            host = new Border { CornerRadius = new CornerRadius(12), Background = Ui.B(Ui.Surface), Padding = new Thickness(11, 7, 11, 7), Child = grid, MinHeight = 34 };
            return box;
        }

        static ScrollViewer Scroll(UIElement content, double maxHeight)
        {
            return new ScrollViewer
            {
                Content = content,
                MaxHeight = maxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
        }

        static TextBlock Empty(string text)
        {
            var t = Ui.Text(text, 12, Ui.B(Ui.Gray), false);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 14, 0, 14);
            return t;
        }

        static string Ago(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return "now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + "m ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + "h ago";
            return (int)span.TotalDays + "d ago";
        }

        // ---------- Clipboard history (memory only, gone when Lumi quits) ----------

        class ClipEntry
        {
            public string Kind, Text;
            public BitmapSource Image;
            public StringCollection Files;
            public Color Swatch;
            public DateTime Time;
        }

        static readonly string[] ClipFilters = { "All", "Text", "Links", "Colours", "Images", "Files" };
        readonly List<ClipEntry> clips = new List<ClipEntry>();
        string clipFilter = "All";
        bool ignoreNextClip;
        StackPanel clipList;

        FrameworkElement BuildClipboardPage()
        {
            clipList = new StackPanel();
            RebuildClips();
            return clipList;
        }

        // Called on every copy. Returns the entry so the pop-up can describe it.
        ClipEntry RecordClip()
        {
            ClipEntry entry = null;
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null) return null;
                double scale = Math.Min(1, 96.0 / Math.Max(image.PixelWidth, image.PixelHeight));
                var thumb = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                thumb.Freeze();
                entry = new ClipEntry { Kind = "Images", Image = thumb, Text = image.PixelWidth + " × " + image.PixelHeight };
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count == 0) return null;
                entry = new ClipEntry { Kind = "Files", Files = files, Text = files.Count == 1 ? System.IO.Path.GetFileName(files[0].TrimEnd('\\')) : files.Count + " files" };
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return null;
                var trimmed = text.Trim();
                Color swatch;
                Uri uri;
                if (TryParseColor(trimmed, out swatch)) entry = new ClipEntry { Kind = "Colours", Text = trimmed, Swatch = swatch };
                else if (Uri.TryCreate(trimmed, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && !trimmed.Contains(" "))
                    entry = new ClipEntry { Kind = "Links", Text = trimmed };
                else entry = new ClipEntry { Kind = "Text", Text = text };
            }
            if (entry == null) return null;
            entry.Time = DateTime.Now;
            if (entry.Text != null) clips.RemoveAll(c => c.Kind == entry.Kind && c.Text == entry.Text && c.Image == null);
            clips.Insert(0, entry);
            if (clips.Count > 40) clips.RemoveAt(clips.Count - 1);
            if (view == ViewExpanded && tab == TabClipboard) RebuildClips();
            return entry;
        }

        static bool TryParseColor(string text, out Color color)
        {
            color = Colors.Transparent;
            var m = Regex.Match(text, @"^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$");
            if (m.Success && (text.StartsWith("#") || Regex.IsMatch(text, "[a-fA-F]")))
            {
                var hex = m.Groups[1].Value;
                if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
                color = Color.FromRgb(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
                return true;
            }
            m = Regex.Match(text, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                color = Color.FromRgb((byte)Math.Min(255, int.Parse(m.Groups[1].Value)), (byte)Math.Min(255, int.Parse(m.Groups[2].Value)), (byte)Math.Min(255, int.Parse(m.Groups[3].Value)));
                return true;
            }
            return false;
        }

        // A link without the tracking: host and path, no query string.
        static string CleanLink(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return url;
            var host = uri.Host.StartsWith("www.") ? uri.Host.Substring(4) : uri.Host;
            var path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
            return host + path;
        }

        void RebuildClips()
        {
            clipList.Children.Clear();
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var f in ClipFilters)
            {
                string filter = f;
                var chip = new Chip(null, f, 24, 11, Ui.C("#F2F2F7"), Colors.Black, delegate { clipFilter = filter; RebuildClips(); RefitPage(TabClipboard); });
                chip.Root.Padding = new Thickness(9, 0, 9, 0);
                chip.Root.Margin = new Thickness(0, 0, 4, 0);
                chip.Selected = clipFilter == f;
                filters.Children.Add(chip.Root);
            }
            clipList.Children.Add(filters);

            var shown = clips.Where(c => clipFilter == "All" || c.Kind == clipFilter).ToList();
            if (shown.Count == 0)
            {
                clipList.Children.Add(Empty(clips.Count == 0 ? "Copy something and it shows up here" : "Nothing of that kind yet"));
                return;
            }
            var list = new StackPanel();
            foreach (var c in shown)
            {
                ClipEntry entry = c;
                var row = Ui.Columns(Ui.Auto, Ui.Star);
                row.Margin = new Thickness(8, 6, 10, 6);
                FrameworkElement preview;
                if (entry.Kind == "Colours")
                    preview = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(entry.Swatch), BorderBrush = Ui.B("#33FFFFFF"), BorderThickness = new Thickness(1) };
                else if (entry.Kind == "Images")
                    preview = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = new ImageBrush(entry.Image) { Stretch = Stretch.UniformToFill } };
                else
                {
                    TextBlock g;
                    var badge = Ui.Badge(28, 8, out g);
                    badge.Background = Ui.B(Ui.Surface);
                    g.Text = entry.Kind == "Links" ? "" : entry.Kind == "Files" ? "" : "";
                    g.FontSize = 12;
                    preview = badge;
                }
                preview.Margin = new Thickness(0, 0, 10, 0);
                Ui.At(row, preview, 0, 0);
                var text = Ui.At(row, new StackPanel { VerticalAlignment = VerticalAlignment.Center }, 1, 0);
                var main = entry.Kind == "Links" ? CleanLink(entry.Text) : string.Join(" ", (entry.Text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                text.Children.Add(Ui.Text(main, 12, Brushes.White, false));
                var kind = entry.Kind == "Colours" ? "Colour" : entry.Kind == "Links" ? "Link" : entry.Kind == "Images" ? "Image" : entry.Kind == "Files" ? "Files" : "Text";
                text.Children.Add(Ui.Text(kind + " · " + Ago(entry.Time), 10.5, Ui.B(Ui.Gray), false));
                var card = Ui.Card(row, 12, delegate { CopyBack(entry); });
                card.Margin = new Thickness(0, 0, 0, 5);
                card.ToolTip = "Click to copy again, right-click to remove";
                card.MouseRightButtonUp += (s, e) => { e.Handled = true; clips.Remove(entry); RebuildClips(); RefitPage(TabClipboard); };
                list.Children.Add(card);
            }
            clipList.Children.Add(Scroll(list, 216));
            var clear = new Chip("", "Clear history", 24, 11, Colors.White, Colors.Black, delegate { clips.Clear(); RebuildClips(); RefitPage(TabClipboard); });
            clear.Root.HorizontalAlignment = HorizontalAlignment.Right;
            clear.Root.Margin = new Thickness(0, 6, 0, 0);
            clipList.Children.Add(clear.Root);
        }

        void CopyBack(ClipEntry entry)
        {
            try
            {
                ignoreNextClip = true;
                if (entry.Kind == "Images" && entry.Image != null) Clipboard.SetImage(entry.Image);
                else if (entry.Kind == "Files" && entry.Files != null) Clipboard.SetFileDropList(entry.Files);
                else Clipboard.SetText(entry.Text);
                ShowToast("", "Copied again");
            }
            catch { ignoreNextClip = false; }
        }

        // A short confirmation in the header, without leaving the page.
        void ShowToast(string glyph, string text)
        {
            titleGlyph.Text = glyph;
            titleText.Text = text;
            var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            restore.Tick += delegate
            {
                restore.Stop();
                titleGlyph.Text = TabGlyphs[tab];
                titleText.Text = TabNames[tab];
            };
            restore.Start();
        }

        // ---------- Notes (saved on this PC) ----------

        class Note
        {
            public string Text;
            public bool Pinned;
        }

        readonly List<Note> notes = new List<Note>();
        StackPanel notesList;
        TextBox noteBox;

        FrameworkElement BuildNotesPage()
        {
            foreach (var line in Store.Read("notes.txt"))
            {
                int bar = line.IndexOf('|');
                if (bar > 0) notes.Add(new Note { Pinned = line.Substring(0, bar) == "1", Text = Store.Unescape(line.Substring(bar + 1)) });
            }
            var page = new StackPanel();
            Border host;
            noteBox = Input("New note — Enter to save, Shift+Enter for a new line", true, out host);
            noteBox.MaxHeight = 80;
            noteBox.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
                e.Handled = true;
                var text = noteBox.Text.Trim();
                if (text.Length == 0) return;
                notes.Insert(0, new Note { Text = text });
                noteBox.Text = "";
                SaveNotes();
            };
            page.Children.Add(host);
            notesList = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            page.Children.Add(Scroll(notesList, 220));
            RebuildNotes();
            return page;
        }

        void SaveNotes()
        {
            Store.Write("notes.txt", notes.Select(n => (n.Pinned ? "1" : "0") + "|" + Store.Escape(n.Text)));
            RebuildNotes();
            RefitPage(TabNotes);
        }

        void RebuildNotes()
        {
            notesList.Children.Clear();
            if (notes.Count == 0) { notesList.Children.Add(Empty("Jot something down. Notes stay on this PC.")); return; }
            foreach (var n in notes.OrderByDescending(n => n.Pinned).ToList())
            {
                Note note = n;
                var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
                row.Margin = new Thickness(6, 7, 6, 7);
                var pin = new Chip("", null, 24, 11, Colors.Transparent, Ui.C("#3A2A10"), Ui.Orange, delegate { note.Pinned = !note.Pinned; SaveNotes(); });
                pin.Root.Width = 24;
                pin.Root.Padding = new Thickness(0);
                pin.Selected = note.Pinned;
                pin.Root.VerticalAlignment = VerticalAlignment.Top;
                Ui.At(row, pin.Root, 0, 0);
                var text = Ui.At(row, Ui.Text(note.Text, 12, Brushes.White, false), 1, 0);
                text.TextWrapping = TextWrapping.Wrap;
                text.MaxHeight = 52;
                text.Margin = new Thickness(6, 3, 6, 0);
                var delete = new Chip("", null, 24, 10, Colors.Transparent, Colors.White, Colors.Black, delegate { notes.Remove(note); SaveNotes(); });
                delete.Root.Width = 24;
                delete.Root.Padding = new Thickness(0);
                delete.Root.VerticalAlignment = VerticalAlignment.Top;
                Ui.At(row, delete.Root, 2, 0);
                var card = Ui.Card(row, 12, delegate
                {
                    try { ignoreNextClip = true; Clipboard.SetText(note.Text); ShowToast("", "Note copied"); } catch { ignoreNextClip = false; }
                });
                card.ToolTip = "Click to copy";
                card.Margin = new Thickness(0, 0, 0, 5);
                notesList.Children.Add(card);
            }
        }

        // ---------- Colour picker ----------

        double pickHue = 210, pickSat = 0.9, pickLight = 0.55;
        readonly List<Color> recentColors = new List<Color>();
        Border pickSwatch, hueTrack, lightTrack;
        TextBlock pickHex, pickRgb, pickHsl;
        TranslateTransform hueKnob, lightKnob;
        UniformGrid recentGrid;
        const double SliderWidth = ContentW - 16;

        FrameworkElement BuildColorPage()
        {
            foreach (var line in Store.Read("colors.txt"))
            {
                Color c;
                if (TryParseColor(line.Trim(), out c)) recentColors.Add(c);
            }
            var page = new StackPanel();
            var top = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            pickSwatch = Ui.At(top, new Border { Width = 62, Height = 62, CornerRadius = new CornerRadius(16), BorderBrush = Ui.B("#33FFFFFF"), BorderThickness = new Thickness(1) }, 0, 0);
            var values = Ui.At(top, new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            pickHex = Ui.Text(20, Brushes.White, true);
            pickRgb = Ui.Text(11.5, Ui.B(Ui.Gray), false);
            pickHsl = Ui.Text(11.5, Ui.B(Ui.Gray), false);
            values.Children.Add(pickHex);
            values.Children.Add(pickRgb);
            values.Children.Add(pickHsl);
            var eyedropper = new Chip("", "Pick", 32, 12, Colors.White, Colors.Black, PickFromScreen);
            eyedropper.Root.ToolTip = "Take any colour off the screen";
            Ui.At(top, eyedropper.Root, 2, 0);
            page.Children.Add(top);

            // Hue, then shade.
            var hue = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++)
            {
                byte r, g, b;
                ColorMath.FromHsl(i * 60 % 360, 1, 0.5, out r, out g, out b);
                hue.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
            }
            hueTrack = new Border { Background = hue };
            page.Children.Add(ColorSlider(hueTrack, out hueKnob, v => { pickHue = v * 360; ShowPick(); }, new Thickness(0, 14, 0, 0)));
            lightTrack = new Border();
            page.Children.Add(ColorSlider(lightTrack, out lightKnob, v => { pickLight = 0.04 + v * 0.92; ShowPick(); }, new Thickness(0, 10, 0, 0)));

            var copy = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(-3, 12, -3, 0) };
            foreach (var format in new[] { "HEX", "RGB", "HSL" })
            {
                string f = format;
                var chip = new Chip("", "Copy " + f, 28, 11.5, Colors.White, Colors.Black, delegate { CopyColor(f); });
                chip.Root.Margin = new Thickness(3, 0, 3, 0);
                copy.Children.Add(chip.Root);
            }
            page.Children.Add(copy);

            var caption = Ui.Text("RECENT", 10.5, Ui.B(Ui.Dim), true);
            caption.Margin = new Thickness(2, 12, 0, 6);
            page.Children.Add(caption);
            recentGrid = new UniformGrid { Rows = 1, Columns = 12 };
            page.Children.Add(recentGrid);
            ShowPick();
            return page;
        }

        Grid ColorSlider(Border track, out TranslateTransform knobTransform, Action<double> onChange, Thickness margin)
        {
            var grid = new Grid { Height = 18, Margin = margin, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            track.Height = 12;
            track.CornerRadius = new CornerRadius(6);
            track.Margin = new Thickness(7, 0, 7, 0);
            grid.Children.Add(track);
            knobTransform = new TranslateTransform();
            grid.Children.Add(new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = Brushes.White,
                StrokeThickness = 2.5,
                Fill = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = knobTransform,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.4 }
            });
            Action<MouseEventArgs> update = e => onChange(Math.Max(0, Math.Min(1, (e.GetPosition(grid).X - 7) / (grid.ActualWidth - 14))));
            grid.MouseLeftButtonDown += (s, e) => { e.Handled = true; grid.CaptureMouse(); update(e); };
            grid.MouseMove += (s, e) => { if (grid.IsMouseCaptured) update(e); };
            grid.MouseLeftButtonUp += (s, e) => { e.Handled = true; grid.ReleaseMouseCapture(); RememberColor(CurrentPick()); };
            return grid;
        }

        Color CurrentPick()
        {
            byte r, g, b;
            ColorMath.FromHsl(pickHue, pickSat, pickLight, out r, out g, out b);
            return Color.FromRgb(r, g, b);
        }

        void SetPick(Color c)
        {
            ColorMath.ToHsl(c.R, c.G, c.B, out pickHue, out pickSat, out pickLight);
            ShowPick();
        }

        void ShowPick()
        {
            var c = CurrentPick();
            pickSwatch.Background = new SolidColorBrush(c);
            pickHex.Text = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            pickRgb.Text = string.Format("rgb({0}, {1}, {2})", c.R, c.G, c.B);
            pickHsl.Text = string.Format(CultureInfo.InvariantCulture, "hsl({0:0}, {1:0}%, {2:0}%)", pickHue, pickSat * 100, pickLight * 100);
            byte dr, dg, db, mr, mg, mb, lr, lg, lb;
            ColorMath.FromHsl(pickHue, pickSat, 0.04, out dr, out dg, out db);
            ColorMath.FromHsl(pickHue, pickSat, 0.5, out mr, out mg, out mb);
            ColorMath.FromHsl(pickHue, pickSat, 0.96, out lr, out lg, out lb);
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            shade.GradientStops.Add(new GradientStop(Color.FromRgb(dr, dg, db), 0));
            shade.GradientStops.Add(new GradientStop(Color.FromRgb(mr, mg, mb), 0.5));
            shade.GradientStops.Add(new GradientStop(Color.FromRgb(lr, lg, lb), 1));
            lightTrack.Background = shade;
            hueKnob.X = pickHue / 360 * SliderWidth;
            lightKnob.X = (pickLight - 0.04) / 0.92 * SliderWidth;
            RebuildRecentColors();
        }

        void RebuildRecentColors()
        {
            recentGrid.Children.Clear();
            foreach (var color in recentColors)
            {
                Color c = color;
                var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(c), BorderBrush = Ui.B("#33FFFFFF"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
                Ui.MakeClickable(dot, delegate { SetPick(c); }, null);
                recentGrid.Children.Add(dot);
            }
            for (int i = recentColors.Count; i < 12; i++)
                recentGrid.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderBrush = Ui.B("#22FFFFFF"), BorderThickness = new Thickness(1) });
        }

        void RememberColor(Color c)
        {
            recentColors.RemoveAll(x => x == c);
            recentColors.Insert(0, c);
            while (recentColors.Count > 12) recentColors.RemoveAt(recentColors.Count - 1);
            Store.Write("colors.txt", recentColors.Select(x => string.Format("#{0:X2}{1:X2}{2:X2}", x.R, x.G, x.B)));
            RebuildRecentColors();
        }

        void CopyColor(string format)
        {
            var text = format == "HEX" ? pickHex.Text : format == "RGB" ? pickRgb.Text : pickHsl.Text;
            try { Clipboard.SetText(text); ShowToast("", text + " copied"); } catch { }
            RememberColor(CurrentPick());
        }

        // Eyedropper: a see-through layer over every screen; click anywhere to take that colour.
        void PickFromScreen()
        {
            pinned = true;
            UpdatePin();
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Topmost = true,
                ShowInTaskbar = false,
                Cursor = Cursors.Cross,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight
            };
            Action<bool> finish = keep =>
            {
                overlay.Close();
                pinned = false;
                UpdatePin();
                if (keep) RememberColor(CurrentPick());
            };
            overlay.MouseMove += delegate { SetPick(Native.PixelUnderCursor()); };
            overlay.MouseLeftButtonDown += delegate { SetPick(Native.PixelUnderCursor()); finish(true); };
            overlay.MouseRightButtonDown += delegate { finish(false); };
            overlay.KeyDown += (s, e) => { if (e.Key == Key.Escape) finish(false); };
            overlay.Show();
            overlay.Activate();
        }

        // ---------- Focus & breaks ----------

        const int PhaseIdle = 0, PhaseFocus = 1, PhaseShort = 2, PhaseLong = 3;
        static readonly int[] FocusLengths = { 15, 25, 45, 60 };
        int focusPhase = PhaseIdle, focusMinutes = 25, focusDone;
        bool focusPaused;
        DateTime focusEnd;
        TimeSpan focusLeft;
        System.Windows.Shapes.Path focusArc, focusMiniArc;
        TextBlock focusTime, focusLabel, focusMiniTime;
        Chip focusStart;
        StackPanel focusSessions, focusLengthRow;
        ScaleTransform focusBreath;

        static Color PhaseColor(int phase)
        {
            return phase == PhaseShort ? Ui.Green : phase == PhaseLong ? Ui.C("#64D2FF") : Ui.C("#FF6B4A");
        }

        FrameworkElement BuildFocusPage()
        {
            var page = Ui.Columns(Ui.Auto, Ui.Star);
            var ring = Ui.At(page, new Grid { Width = 124, Height = 124, RenderTransformOrigin = new Point(0.5, 0.5) }, 0, 0);
            focusBreath = new ScaleTransform(1, 1);
            ring.RenderTransform = focusBreath;
            ring.Children.Add(new Ellipse { Stroke = Ui.B("#26FFFFFF"), StrokeThickness = 8 });
            focusArc = new System.Windows.Shapes.Path { StrokeThickness = 8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = new SolidColorBrush(PhaseColor(PhaseFocus)) };
            ring.Children.Add(focusArc);
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            focusTime = Ui.Text(26, Brushes.White, true);
            focusTime.HorizontalAlignment = HorizontalAlignment.Center;
            focusLabel = Ui.Text(10, Ui.B(Ui.Gray), true);
            focusLabel.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(focusTime);
            center.Children.Add(focusLabel);
            ring.Children.Add(center);

            var side = Ui.At(page, new StackPanel { Margin = new Thickness(18, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            side.Children.Add(Ui.Text("Work in focused sessions with short breaks between, and a longer break every four.", 11.5, Ui.B(Ui.Gray), false));
            ((TextBlock)side.Children[0]).TextWrapping = TextWrapping.Wrap;
            focusSessions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            side.Children.Add(focusSessions);
            focusLengthRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            side.Children.Add(focusLengthRow);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            focusStart = new Chip("", "Start", 32, 12, Colors.White, Colors.Black, ToggleFocus);
            focusStart.Selected = true;
            var skip = new Chip("", "Skip", 32, 12, Colors.White, Colors.Black, delegate { if (focusPhase != PhaseIdle) NextPhase(false); });
            skip.Root.Margin = new Thickness(6, 0, 0, 0);
            var reset = new Chip("", null, 32, 12, Colors.White, Colors.Black, delegate { focusPhase = PhaseIdle; focusPaused = false; focusDone = 0; UpdateFocus(); UpdateMode(); });
            reset.Root.Width = 32;
            reset.Root.Padding = new Thickness(0);
            reset.Root.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(focusStart.Root);
            buttons.Children.Add(skip.Root);
            buttons.Children.Add(reset.Root);
            side.Children.Add(buttons);
            UpdateFocus();
            return page;
        }

        // Compact: a little progress ring on the left, the time left on the right.
        Grid BuildFocusCompact()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = FocusW;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var ring = Ui.At(g, new Grid { Width = 18, Height = 18, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            ring.Children.Add(new Ellipse { Stroke = Ui.B("#33FFFFFF"), StrokeThickness = 2.5 });
            focusMiniArc = new System.Windows.Shapes.Path { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = new SolidColorBrush(PhaseColor(PhaseFocus)) };
            ring.Children.Add(focusMiniArc);
            focusMiniTime = Ui.At(g, Ui.Text(12.5, Brushes.White, true), 2, 0);
            focusMiniTime.Margin = new Thickness(0, 0, 15, 0);
            return g;
        }

        TimeSpan PhaseLength(int phase)
        {
            return TimeSpan.FromMinutes(phase == PhaseShort ? 5 : phase == PhaseLong ? 15 : focusMinutes);
        }

        TimeSpan FocusRemaining()
        {
            if (focusPhase == PhaseIdle) return PhaseLength(PhaseFocus);
            var left = focusPaused ? focusLeft : focusEnd - DateTime.Now;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        void ToggleFocus()
        {
            if (focusPhase == PhaseIdle)
            {
                focusPhase = PhaseFocus;
                focusPaused = false;
                focusEnd = DateTime.Now + PhaseLength(PhaseFocus);
            }
            else if (focusPaused)
            {
                focusPaused = false;
                focusEnd = DateTime.Now + focusLeft;
            }
            else
            {
                focusPaused = true;
                focusLeft = focusEnd - DateTime.Now;
            }
            UpdateFocus();
            UpdateMode();
        }

        // Focus -> short break (every fourth: long break) -> focus, each starting on its own.
        void NextPhase(bool announce)
        {
            if (focusPhase == PhaseFocus)
            {
                focusDone++;
                focusPhase = focusDone % 4 == 0 ? PhaseLong : PhaseShort;
            }
            else focusPhase = PhaseFocus;
            focusPaused = false;
            focusEnd = DateTime.Now + PhaseLength(focusPhase);
            if (announce)
            {
                SystemSounds.Asterisk.Play();
                ShowPeek(null, null, "", PhaseColor(focusPhase),
                    focusPhase == PhaseFocus ? "Back to focus" : focusPhase == PhaseLong ? "Long break" : "Break time",
                    focusPhase == PhaseFocus ? focusMinutes + " minutes of focus" : (int)PhaseLength(focusPhase).TotalMinutes + " minutes — stretch, drink some water", false);
            }
            UpdateFocus();
            UpdateMode();
        }

        void FocusTick()
        {
            if (focusPhase != PhaseIdle && !focusPaused && DateTime.Now >= focusEnd) NextPhase(true);
            else if (focusPhase != PhaseIdle) UpdateFocus();
        }

        void UpdateFocus()
        {
            var left = FocusRemaining();
            var total = PhaseLength(focusPhase == PhaseIdle ? PhaseFocus : focusPhase);
            double done = focusPhase == PhaseIdle ? 0 : 1 - left.TotalSeconds / total.TotalSeconds;
            var color = PhaseColor(focusPhase == PhaseIdle ? PhaseFocus : focusPhase);
            focusTime.Text = string.Format("{0}:{1:00}", (int)left.TotalMinutes, left.Seconds);
            focusLabel.Text = focusPhase == PhaseIdle ? "READY" : focusPaused ? "PAUSED" : focusPhase == PhaseFocus ? "FOCUS" : focusPhase == PhaseLong ? "LONG BREAK" : "BREAK";
            focusArc.Data = done > 0 ? RingArc(124, 8, done) : null;
            ((SolidColorBrush)focusArc.Stroke).Color = color;
            focusMiniArc.Data = RingArc(18, 2.5, Math.Max(0.02, done));
            ((SolidColorBrush)focusMiniArc.Stroke).Color = color;
            focusMiniTime.Text = focusTime.Text;
            focusMiniTime.Foreground = new SolidColorBrush(color);
            focusStart.SetGlyph(focusPhase != PhaseIdle && !focusPaused ? "" : "");
            ((TextBlock)((StackPanel)focusStart.Root.Child).Children[1]).Text = focusPhase == PhaseIdle ? "Start" : focusPaused ? "Resume" : "Pause";

            // A ring that breathes while a session runs.
            bool breathing = focusPhase != PhaseIdle && !focusPaused;
            if (breathing && focusBreath.HasAnimatedProperties == false)
            {
                var breath = new DoubleAnimation(1, 1.035, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                focusBreath.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
                focusBreath.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
            }
            else if (!breathing)
            {
                focusBreath.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                focusBreath.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            focusSessions.Children.Clear();
            for (int i = 0; i < 4; i++)
                focusSessions.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), Fill = i < focusDone % 4 || (focusDone > 0 && focusDone % 4 == 0 && focusPhase == PhaseLong) ? new SolidColorBrush(PhaseColor(PhaseFocus)) : Ui.B("#33FFFFFF") });
            var sessions = Ui.Text(focusDone + (focusDone == 1 ? " session" : " sessions") + " today", 10.5, Ui.B(Ui.Gray), false);
            sessions.Margin = new Thickness(4, 0, 0, 0);
            focusSessions.Children.Add(sessions);

            focusLengthRow.Children.Clear();
            focusLengthRow.Visibility = focusPhase == PhaseIdle ? Visibility.Visible : Visibility.Collapsed;
            foreach (var m in FocusLengths)
            {
                int minutes = m;
                var chip = new Chip(null, m + "m", 24, 11, Ui.C("#F2F2F7"), Colors.Black, delegate { focusMinutes = minutes; UpdateFocus(); });
                chip.Root.Padding = new Thickness(9, 0, 9, 0);
                chip.Root.Margin = new Thickness(0, 0, 4, 0);
                chip.Selected = focusMinutes == m;
                focusLengthRow.Children.Add(chip.Root);
            }
        }

        // ---------- Todos with reminders ----------

        class Todo
        {
            public string Text;
            public DateTime? Due;
            public bool Done, Notified;
        }

        readonly List<Todo> todos = new List<Todo>();
        StackPanel todoList;
        TextBox todoBox;
        TextBlock todoPreview;

        FrameworkElement BuildTodosPage()
        {
            foreach (var line in Store.Read("todos.txt"))
            {
                var parts = line.Split(new[] { '|' }, 4);
                if (parts.Length < 4) continue;
                long ticks;
                long.TryParse(parts[1], out ticks);
                todos.Add(new Todo { Done = parts[0] == "1", Due = ticks > 0 ? new DateTime(ticks) : (DateTime?)null, Notified = parts[2] == "1", Text = Store.Unescape(parts[3]) });
            }
            var page = new StackPanel();
            Border host;
            todoBox = Input("Add a todo — try “pay rent tomorrow 5pm”", false, out host);
            todoPreview = Ui.Text(11, Ui.B(Ui.Orange), true);
            todoPreview.Margin = new Thickness(4, 5, 0, 0);
            todoPreview.Visibility = Visibility.Collapsed;
            todoBox.TextChanged += delegate
            {
                DateTime? due;
                TodoParser.Parse(todoBox.Text, DateTime.Now, out due);
                todoPreview.Visibility = due.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (due.HasValue) todoPreview.Text = "⏰ " + TodoParser.Describe(due.Value, DateTime.Now);
                RefitPage(TabTodos);
            };
            todoBox.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (todoBox.Text.Trim().Length == 0) return;
                DateTime? due;
                var text = TodoParser.Parse(todoBox.Text, DateTime.Now, out due);
                todos.Add(new Todo { Text = text, Due = due });
                todoBox.Text = "";
                SaveTodos();
            };
            page.Children.Add(host);
            page.Children.Add(todoPreview);
            todoList = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            page.Children.Add(Scroll(todoList, 220));
            RebuildTodos();
            return page;
        }

        void SaveTodos()
        {
            Store.Write("todos.txt", todos.Select(t => (t.Done ? "1" : "0") + "|" + (t.Due.HasValue ? t.Due.Value.Ticks : 0) + "|" + (t.Notified ? "1" : "0") + "|" + Store.Escape(t.Text)));
            RebuildTodos();
            RebuildCalendar();
            RefitPage(TabTodos);
        }

        void RebuildTodos()
        {
            todoList.Children.Clear();
            if (todos.Count == 0) { todoList.Children.Add(Empty("Nothing to do. Nice.")); return; }
            var now = DateTime.Now;
            foreach (var t in todos.OrderBy(t => t.Done).ThenBy(t => t.Due ?? DateTime.MaxValue).ToList())
            {
                Todo todo = t;
                var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
                row.Margin = new Thickness(10, 8, 10, 8);
                var check = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                check.Children.Add(new Ellipse { Stroke = todo.Done ? Ui.B(Ui.Green) : Ui.B("#66FFFFFF"), StrokeThickness = 1.5, Fill = todo.Done ? Ui.B(Ui.Green) : Brushes.Transparent });
                if (todo.Done) check.Children.Add(Ui.Glyph("", 10, Brushes.Black));
                Ui.At(row, check, 0, 0);
                var text = Ui.At(row, Ui.Text(todo.Text, 12.5, todo.Done ? Ui.B(Ui.Gray) : Brushes.White, false), 1, 0);
                if (todo.Done) text.TextDecorations = TextDecorations.Strikethrough;
                if (todo.Due.HasValue)
                {
                    bool overdue = !todo.Done && todo.Due.Value < now;
                    var chip = Ui.At(row, new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = Ui.B(overdue ? "#33FF453A" : "#1FFFFFFF"),
                        Padding = new Thickness(7, 2, 7, 2),
                        Margin = new Thickness(8, 0, 0, 0),
                        Child = Ui.Text(TodoParser.Describe(todo.Due.Value, now), 10.5, Ui.B(overdue ? Ui.Red : Ui.Gray), true)
                    }, 2, 0);
                    chip.VerticalAlignment = VerticalAlignment.Center;
                }
                var card = Ui.Card(row, 12, delegate { todo.Done = !todo.Done; SaveTodos(); });
                card.ToolTip = "Click to tick off, right-click to delete";
                card.Margin = new Thickness(0, 0, 0, 5);
                card.MouseRightButtonUp += (s, e) => { e.Handled = true; todos.Remove(todo); SaveTodos(); };
                todoList.Children.Add(card);
            }
            if (todos.Any(t => t.Done))
            {
                var clear = new Chip("", "Clear completed", 24, 11, Colors.White, Colors.Black, delegate { todos.RemoveAll(t => t.Done); SaveTodos(); });
                clear.Root.HorizontalAlignment = HorizontalAlignment.Right;
                clear.Root.Margin = new Thickness(0, 2, 0, 0);
                todoList.Children.Add(clear.Root);
            }
        }

        // Reminders: a pop-up and a chime when a todo comes due.
        void TodoTick()
        {
            var now = DateTime.Now;
            var due = todos.FirstOrDefault(t => !t.Done && !t.Notified && t.Due.HasValue && t.Due.Value <= now);
            if (due == null) return;
            due.Notified = true;
            SaveTodos();
            SystemSounds.Exclamation.Play();
            ShowPeek(null, null, "", Ui.Orange, due.Text, "Reminder · " + TodoParser.Describe(due.Due.Value, now), false);
            peekTimer.Interval = TimeSpan.FromSeconds(6);
            peekTimer.Stop();
            peekTimer.Start();
        }

        // ---------- Devices ----------

        StackPanel deviceList;

        FrameworkElement BuildDevicesPage()
        {
            deviceList = new StackPanel();
            deviceList.Children.Add(Empty("Looking for devices…"));
            return deviceList;
        }

        void OnDevices(List<DeviceRow> devices)
        {
            deviceList.Children.Clear();
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            if ((power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && power.BatteryLifePercent <= 1)
                deviceList.Children.Add(DeviceCard("", "This PC", power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "Charging" : "On battery",
                    (int)Math.Round(power.BatteryLifePercent * 100), power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online));
            foreach (var d in devices)
                deviceList.Children.Add(DeviceCard(d.Glyph, d.Name, d.Connected ? "Connected" : "Not connected", d.Connected ? d.Battery : -1, false));
            if (devices.Count == 0) deviceList.Children.Add(Empty("No Bluetooth devices paired"));
            RefitPage(TabDevices);
        }

        static Border DeviceCard(string glyph, string name, string status, int battery, bool charging)
        {
            var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            row.Margin = new Thickness(8, 7, 12, 7);
            TextBlock g;
            var badge = Ui.At(row, Ui.Badge(32, 16, out g), 0, 0);
            badge.Background = Ui.B(status == "Not connected" ? "#14FFFFFF" : "#2C2C2E");
            g.Text = glyph;
            g.FontSize = 14;
            g.Foreground = status == "Not connected" ? Ui.B(Ui.Gray) : Brushes.White;
            badge.Margin = new Thickness(0, 0, 10, 0);
            var text = Ui.At(row, new StackPanel { VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            text.Children.Add(Ui.Text(name, 12.5, Brushes.White, true));
            text.Children.Add(Ui.Text(status, 10.5, Ui.B(status == "Connected" || status == "Charging" ? Ui.Green : Ui.Gray), false));
            if (battery >= 0)
            {
                var ring = Ui.At(row, new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center }, 2, 0);
                ring.Children.Add(new Ellipse { Stroke = Ui.B("#33FFFFFF"), StrokeThickness = 3 });
                ring.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = RingArc(32, 3, battery / 100.0),
                    Stroke = Ui.B(battery <= 20 ? Ui.Red : Ui.Green),
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
                var value = Ui.Text(charging ? "⚡" : battery.ToString(CultureInfo.CurrentCulture), 10, Brushes.White, true);
                value.HorizontalAlignment = HorizontalAlignment.Center;
                ring.Children.Add(value);
                ring.ToolTip = battery + "%";
            }
            var card = Ui.Card(row, 12, null);
            card.Margin = new Thickness(0, 0, 0, 5);
            return card;
        }

        // ---------- Dev servers ----------

        StackPanel devList;

        FrameworkElement BuildDevPage()
        {
            devList = new StackPanel();
            devList.Children.Add(Empty("Looking for dev servers…"));
            return devList;
        }

        void OnDevServers(List<DevServer> servers)
        {
            devList.Children.Clear();
            if (servers.Count == 0)
            {
                devList.Children.Add(Empty("No dev servers running"));
                var hint = Ui.Text("Start one (npm run dev, python -m http.server…) and it shows up here", 11, Ui.B(Ui.Dim), false);
                hint.HorizontalAlignment = HorizontalAlignment.Center;
                hint.Margin = new Thickness(0, -8, 0, 8);
                devList.Children.Add(hint);
            }
            foreach (var s in servers)
            {
                DevServer server = s;
                var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto);
                row.Margin = new Thickness(10, 7, 8, 7);
                var port = Ui.At(row, Ui.Text(":" + server.Port, 15, Ui.B(Ui.Green), true), 0, 0);
                port.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                port.Margin = new Thickness(0, 0, 12, 0);
                var text = Ui.At(row, new StackPanel { VerticalAlignment = VerticalAlignment.Center }, 1, 0);
                text.Children.Add(Ui.Text(string.IsNullOrEmpty(server.Project) ? server.Tool : server.Project, 12.5, Brushes.White, true));
                text.Children.Add(Ui.Text(server.Tool + " · " + server.Process + " " + server.Pid, 10.5, Ui.B(Ui.Gray), false));
                var open = new Chip("", null, 28, 12, Colors.White, Colors.Black, delegate { Ui.Open("http://localhost:" + server.Port, null); });
                open.Root.Width = 28;
                open.Root.Padding = new Thickness(0);
                open.Root.ToolTip = "Open in browser";
                Ui.At(row, open.Root, 2, 0);
                // Stopping takes two clicks, so a stray click can't kill your server.
                Chip stop = null;
                bool armed = false;
                stop = new Chip("", null, 28, 11, Ui.C("#33FF453A"), Ui.Red, Colors.White, delegate
                {
                    if (!armed)
                    {
                        armed = true;
                        stop.Selected = true;
                        stop.Root.ToolTip = "Click again to stop";
                        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        reset.Tick += delegate { reset.Stop(); armed = false; stop.Selected = false; stop.Root.ToolTip = "Stop server"; };
                        reset.Start();
                        return;
                    }
                    DevServerScanner.Stop(server.Pid);
                    ShowToast("", "Stopped :" + server.Port);
                    devServers.Refresh();
                });
                stop.Root.Width = 28;
                stop.Root.Padding = new Thickness(0);
                stop.Root.Margin = new Thickness(6, 0, 0, 0);
                stop.Root.ToolTip = "Stop server";
                Ui.At(row, stop.Root, 3, 0);
                var card = Ui.Card(row, 12, null);
                card.Margin = new Thickness(0, 0, 0, 5);
                devList.Children.Add(card);
            }
            RefitPage(TabDev);
        }

        // ---------- Teleprompter ----------

        readonly List<string> promptLines = new List<string>();
        int promptIndex, promptWpm = 140;
        bool prompting;
        DateTime promptNext;
        DispatcherTimer promptTimer;
        StackPanel prompterHost;
        TextBlock prompterLine;

        FrameworkElement BuildPrompterPage()
        {
            prompterHost = new StackPanel();
            promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            promptTimer.Tick += delegate
            {
                if (!prompting || DateTime.Now < promptNext) return;
                if (promptIndex >= promptLines.Count - 1) { StopPrompting(); return; }
                ShowPromptLine(promptIndex + 1);
            };
            RebuildPrompter();
            return prompterHost;
        }

        // Compact while reading: the current line sits right under the camera.
        Grid BuildPrompterCompact()
        {
            var g = new Grid { Width = PrompterW, HorizontalAlignment = HorizontalAlignment.Center };
            prompterLine = Ui.Text(13.5, Brushes.White, true);
            prompterLine.HorizontalAlignment = HorizontalAlignment.Center;
            prompterLine.Margin = new Thickness(20, 0, 20, 0);
            g.Children.Add(prompterLine);
            return g;
        }

        // Script -> short lines of a few words, breaking at punctuation where possible.
        void LoadScript(string script)
        {
            promptLines.Clear();
            var words = script.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var line = new List<string>();
            foreach (var w in words)
            {
                line.Add(w);
                int length = string.Join(" ", line).Length;
                if (length >= 44 || (line.Count >= 4 && Regex.IsMatch(w, @"[.!?;:,]$")))
                {
                    promptLines.Add(string.Join(" ", line));
                    line.Clear();
                }
            }
            if (line.Count > 0) promptLines.Add(string.Join(" ", line));
            promptIndex = 0;
        }

        TimeSpan LineDuration(string line)
        {
            int words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            return TimeSpan.FromMinutes(Math.Max(1, words) / (double)promptWpm);
        }

        void ShowPromptLine(int index)
        {
            promptIndex = Math.Max(0, Math.Min(promptLines.Count - 1, index));
            promptNext = DateTime.Now + LineDuration(promptLines[promptIndex]);
            prompterLine.Text = promptLines[promptIndex];
            RebuildPrompter();
        }

        void StartPrompting()
        {
            if (promptLines.Count == 0) return;
            prompting = true;
            ShowPromptLine(promptIndex);
            promptTimer.Start();
            pinned = false;
            UpdatePin();
            SetView(ViewCompact);
            UpdateMode();
        }

        void StopPrompting()
        {
            prompting = false;
            promptTimer.Stop();
            RebuildPrompter();
            UpdateMode();
        }

        void RebuildPrompter()
        {
            prompterHost.Children.Clear();
            if (promptLines.Count == 0)
            {
                prompterHost.Children.Add(Empty("Paste your script and read it from under the camera"));
                var paste = new Chip("", "Paste script", 30, 12, Colors.White, Colors.Black, PasteScript);
                paste.Root.HorizontalAlignment = HorizontalAlignment.Center;
                prompterHost.Children.Add(paste.Root);
                RefitPage(TabPrompter);
                return;
            }

            Func<int, double, Brush, TextBlock> line = (i, size, brush) =>
            {
                var t = Ui.Text(i >= 0 && i < promptLines.Count ? promptLines[i] : "", size, brush, i == promptIndex);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.TextWrapping = TextWrapping.Wrap;
                t.TextAlignment = TextAlignment.Center;
                t.Margin = new Thickness(0, 2, 0, 2);
                return t;
            };
            prompterHost.Children.Add(line(promptIndex - 1, 12, Ui.B(Ui.Dim)));
            prompterHost.Children.Add(line(promptIndex, 16, Brushes.White));
            prompterHost.Children.Add(line(promptIndex + 1, 12, Ui.B(Ui.Gray)));

            var remaining = TimeSpan.FromMinutes(promptLines.Skip(promptIndex).Sum(l => l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length) / (double)promptWpm);
            var status = Ui.Text("Line " + (promptIndex + 1) + " of " + promptLines.Count + " · about " + Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + " min left", 10.5, Ui.B(Ui.Gray), false);
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 8, 0, 10);
            prompterHost.Children.Add(status);

            var controls = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star);
            var speed = Ui.At(controls, new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            var slower = new Chip("", null, 24, 10, Colors.White, Colors.Black, delegate { promptWpm = Math.Max(60, promptWpm - 10); RebuildPrompter(); });
            var faster = new Chip("", null, 24, 10, Colors.White, Colors.Black, delegate { promptWpm = Math.Min(260, promptWpm + 10); RebuildPrompter(); });
            slower.Root.Width = faster.Root.Width = 24;
            slower.Root.Padding = faster.Root.Padding = new Thickness(0);
            speed.Children.Add(slower.Root);
            var wpm = Ui.Text(promptWpm + " wpm", 11, Ui.B("#D1D1D6"), true);
            wpm.Margin = new Thickness(6, 0, 6, 0);
            speed.Children.Add(wpm);
            speed.Children.Add(faster.Root);
            var back = new Chip("", null, 32, 13, Colors.White, Colors.Black, delegate { ShowPromptLine(promptIndex - 1); });
            var play = new Chip(prompting ? "" : "", null, 38, 15, Colors.White, Colors.Black, delegate { if (prompting) StopPrompting(); else StartPrompting(); });
            var forward = new Chip("", null, 32, 13, Colors.White, Colors.Black, delegate { ShowPromptLine(promptIndex + 1); });
            play.Selected = true;
            foreach (var c in new[] { back, play, forward }) { c.Root.Width = c.Root.Height; c.Root.Padding = new Thickness(0); }
            play.Root.Margin = new Thickness(10, 0, 10, 0);
            Ui.At(controls, back.Root, 1, 0);
            Ui.At(controls, play.Root, 2, 0);
            Ui.At(controls, forward.Root, 3, 0);
            var paste2 = new Chip("", "New", 24, 11, Colors.White, Colors.Black, PasteScript);
            paste2.Root.HorizontalAlignment = HorizontalAlignment.Right;
            paste2.Root.Padding = new Thickness(9, 0, 9, 0);
            Ui.At(controls, paste2.Root, 4, 0);
            prompterHost.Children.Add(controls);
            RefitPage(TabPrompter);
        }

        void PasteScript()
        {
            try
            {
                if (!Clipboard.ContainsText()) { ShowToast("", "Copy your script first"); return; }
                StopPrompting();
                LoadScript(Clipboard.GetText());
                if (promptLines.Count > 0) prompterLine.Text = promptLines[0];
                RebuildPrompter();
            }
            catch { }
        }
    }
}
