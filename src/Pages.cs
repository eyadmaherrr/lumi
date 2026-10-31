// The open island's dashboard: a header (current app, pin, battery, clock), the app pages,
// a launcher grid of every app, and page dots. Scroll (or swipe sideways) to flip between apps.
// The newer productivity pages live in Pages.Tools.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace Lumi
{
    partial class IslandWindow
    {
        const int TabAwake = 0, TabMusic = 1, TabAi = 2, TabFocus = 3, TabTodos = 4, TabNotes = 5, TabClipboard = 6, TabCalendar = 7,
            TabWeather = 8, TabSystem = 9, TabDevices = 10, TabShelf = 11, TabColor = 12, TabDev = 13, TabPrompter = 14, TabCount = 15;
        static readonly string[] TabNames =
        {
            "Always On", "Music", "AI", "Focus", "Todos", "Notes", "Clipboard", "Calendar", "Weather", "System", "Devices", "Shelf", "Colour", "Dev servers", "Prompter"
        };
        static readonly string[] TabGlyphs =
        {
            "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""
        };
        static readonly TimeSpan?[] Durations =
        {
            null, TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8)
        };
        static readonly string[] DurationLabels = { "∞", "30m", "1h", "2h", "4h", "8h" };
        static readonly Color MusicGreen = Ui.C("#1ED760"), Track = Ui.C("#3A3A3C");

        int tab = TabAwake;
        bool launcherOpen;
        FrameworkElement[] pages;
        FrameworkElement launcher;
        TextBlock clockText, batteryText, titleText, titleGlyph, pinGlyph;
        StackPanel dots;

        Grid BuildExpanded()
        {
            expandedScale = new ScaleTransform(0.94, 0.94);
            var g = Ui.Rows(3);
            g.Width = ContentW;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Top;
            g.Margin = new Thickness(0, 12, 0, 0);
            g.RenderTransformOrigin = new Point(0.5, 0);
            g.RenderTransform = expandedScale;

            // Header: the current app (click for every app), then pin, battery and clock in the wing.
            var header = Ui.At(g, Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto, Ui.Auto), 0, 0);
            header.Height = 28;
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
            titleGlyph = Ui.Glyph("", 13, Brushes.White);
            titleGlyph.Margin = new Thickness(0, 0, 8, 0);
            titleText = Ui.Text(13, Brushes.White, true);
            var chevron = Ui.Glyph("", 9, Ui.B(Ui.Gray));
            chevron.Margin = new Thickness(7, 2, 0, 0);
            title.Children.Add(titleGlyph);
            title.Children.Add(titleText);
            title.Children.Add(chevron);
            var titleChip = Ui.Card(title, 14, ToggleLauncher);
            titleChip.Height = 28;
            titleChip.Background = Brushes.Transparent;
            titleChip.MouseEnter += delegate { titleChip.Background = Ui.B(Ui.Surface); };
            titleChip.MouseLeave += delegate { titleChip.Background = Brushes.Transparent; };
            Ui.At(header, titleChip, 0, 0);
            pinGlyph = Ui.At(header, Ui.Glyph("", 12, Ui.B(Ui.Orange)), 2, 0);
            pinGlyph.Margin = new Thickness(0, 0, 10, 0);
            pinGlyph.Visibility = Visibility.Collapsed;
            batteryText = Ui.At(header, Ui.Text(12, Ui.B(Ui.Gray), false), 3, 0);
            batteryText.Margin = new Thickness(0, 0, 10, 0);
            clockText = Ui.At(header, Ui.Text(12.5, Brushes.White, true), 4, 0);
            clockText.Margin = new Thickness(0, 0, 4, 0);

            pages = new FrameworkElement[]
            {
                BuildAwakePage(), BuildMusicPage(), BuildAiPage(), BuildFocusPage(), BuildTodosPage(), BuildNotesPage(), BuildClipboardPage(),
                BuildCalendarPage(), BuildWeatherPage(), BuildSystemPage(), BuildDevicesPage(), BuildShelfPage(), BuildColorPage(), BuildDevPage(), BuildPrompterPage()
            };
            launcher = BuildLauncher();
            foreach (var page in pages.Concat(new[] { launcher }))
            {
                page.Margin = new Thickness(0, 14, 0, 0);
                page.VerticalAlignment = VerticalAlignment.Top;
                Ui.Hidden(page);
                Ui.At(g, page, 0, 1);
            }

            dots = Ui.At(g, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) }, 0, 2);
            for (int i = 0; i < TabCount; i++)
            {
                int index = i;
                var dot = new Border { Height = 5, Width = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(2, 0, 2, 0), Background = Ui.B("#55FFFFFF"), Cursor = Cursors.Hand };
                Ui.MakeClickable(dot, delegate { SetTab(index); }, null);
                dots.Children.Add(dot);
            }
            return g;
        }

        // Every app as a grid of icons, like an app library.
        FrameworkElement BuildLauncher()
        {
            var grid = new UniformGrid { Columns = 5, Margin = new Thickness(-3, 0, -3, 0) };
            for (int i = 0; i < TabCount; i++)
            {
                int index = i;
                var tile = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
                TextBlock glyph;
                var badge = Ui.Badge(42, 13, out glyph);
                badge.Background = Ui.B(Ui.Surface);
                glyph.Text = TabGlyphs[i];
                glyph.FontSize = 17;
                badge.HorizontalAlignment = HorizontalAlignment.Center;
                tile.Children.Add(badge);
                var label = Ui.Text(TabNames[i], 10.5, Ui.B("#D1D1D6"), false);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 5, 0, 0);
                tile.Children.Add(label);
                var card = Ui.Card(tile, 14, delegate { SetTab(index); });
                card.Background = Brushes.Transparent;
                card.Margin = new Thickness(3, 0, 3, 0);
                grid.Children.Add(card);
            }
            return grid;
        }

        void ToggleLauncher()
        {
            launcherOpen = !launcherOpen;
            Ui.Reveal(launcher, launcherOpen);
            Ui.Reveal(pages[tab], !launcherOpen);
            ShowOnly(launcherOpen ? launcher : pages[tab]);
            if (launcherOpen) Ui.Cascade(launcher);
            UpdateServices();
            RefitPage(tab);
        }

        // Pages share one grid cell; hidden ones are collapsed so they don't stretch the island
        // to the height of the tallest page.
        void ShowOnly(FrameworkElement visible)
        {
            foreach (var page in pages.Concat(new[] { launcher }))
                page.Visibility = page == visible ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetTab(int index)
        {
            tab = index;
            if (launcherOpen)
            {
                launcherOpen = false;
                Ui.Reveal(launcher, false);
            }
            for (int i = 0; i < pages.Length; i++) Ui.Reveal(pages[i], i == index);
            ShowOnly(pages[index]);
            Ui.Cascade(pages[index]);
            titleText.Text = TabNames[index];
            titleGlyph.Text = TabGlyphs[index];
            for (int i = 0; i < dots.Children.Count; i++)
            {
                var dot = (Border)dots.Children[i];
                Ui.Animate(dot, WidthProperty, i == index ? 14 : 5, 220);
                dot.Background = i == index ? Brushes.White : Ui.B("#55FFFFFF");
            }
            UpdateServices();
            if (index == TabWeather) weather.Refresh(false);
            if (index == TabClipboard) RebuildClips();
            UpdateWash();
            if (view == ViewExpanded)
            {
                height.Target = ExpandedHeight(index);
                StartAnimation();
            }
        }

        // Stats, AI tools, dev servers and the device list only poll while you're looking at them.
        void UpdateServices()
        {
            bool open = view == ViewExpanded && !launcherOpen;
            stats.Active = open && tab == TabSystem;
            aiTools.Active = open && tab == TabAi;
            devServers.Active = open && tab == TabDev;
            bluetooth.ListActive = open && tab == TabDevices;
        }

        void UpdatePin()
        {
            pinGlyph.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Always On ----------

        Border iconCircle;
        DropShadowEffect iconGlow;
        SolidColorBrush trackBrush;
        TextBlock subtitle;
        TranslateTransform knob;
        Chip[] durationChips;
        Chip screenChip, startupChip;

        FrameworkElement BuildAwakePage()
        {
            var g = Ui.Rows(4);
            var header = Ui.At(g, Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto), 0, 0);
            iconGlow = new DropShadowEffect { Color = Ui.Green, BlurRadius = 20, ShadowDepth = 0, Opacity = 0.6 };
            TextBlock glyph;
            iconCircle = Ui.At(header, Ui.Badge(44, 22, out glyph), 0, 0);
            glyph.Text = "";
            iconCircle.Effect = iconGlow;

            var text = Ui.At(header, new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            text.Children.Add(Ui.Text("Always On", 15, Brushes.White, true));
            subtitle = Ui.Text(12, Ui.B(Ui.Gray), false);
            subtitle.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(subtitle);

            trackBrush = new SolidColorBrush(Ui.Green);
            knob = new TranslateTransform(20, 0);
            var toggle = Ui.At(header, new Border
            {
                Width = 48,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = trackBrush,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(3, 0, 0, 0),
                    RenderTransform = knob,
                    Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3 }
                }
            }, 2, 0);
            Ui.MakeClickable(toggle, delegate { SetAwake(!awake, durationIndex, false); }, null);

            var caption = Ui.At(g, Ui.Text("KEEP AWAKE FOR", 10.5, Ui.B(Ui.Dim), true), 0, 1);
            caption.Margin = new Thickness(2, 16, 0, 7);

            var chips = Ui.At(g, new UniformGrid { Rows = 1, Columns = Durations.Length, Margin = new Thickness(-3, 0, -3, 0) }, 0, 2);
            durationChips = new Chip[Durations.Length];
            for (int i = 0; i < Durations.Length; i++)
            {
                int index = i;
                var chip = new Chip(null, DurationLabels[i], 30, i == 0 ? 15 : 12.5, Ui.C("#F2F2F7"), Colors.Black, delegate { SetAwake(true, index, false); });
                chip.Root.Margin = new Thickness(3, 0, 3, 0);
                chip.Root.Padding = new Thickness(0);
                durationChips[i] = chip;
                chips.Children.Add(chip.Root);
            }

            var bottom = Ui.At(g, Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star, Ui.Auto), 0, 3);
            bottom.Margin = new Thickness(0, 12, 0, 0);
            screenChip = new Chip(null, "Screen stays on", 26, 11.5, Ui.C("#163A22"), Ui.Green, delegate
            {
                screenOn = !screenOn;
                ApplyPower();
                Refresh();
                SaveSettings();
            });
            Ui.At(bottom, screenChip.Root, 0, 0);
            startupChip = new Chip(null, "Start with Windows", 26, 11.5, Ui.C("#163A22"), Ui.Green, delegate
            {
                SetStartup(!GetStartup());
                Refresh();
            });
            startupChip.Root.Margin = new Thickness(6, 0, 0, 0);
            Ui.At(bottom, startupChip.Root, 1, 0);

            var quitBrush = new SolidColorBrush(Ui.Gray);
            var quit = Ui.At(bottom, Ui.Text("Quit", 12, quitBrush, false), 3, 0);
            quit.Background = Brushes.Transparent;
            quit.Padding = new Thickness(8, 4, 2, 4);
            quit.Cursor = Cursors.Hand;
            quit.MouseEnter += delegate { Ui.AnimateColor(quitBrush, Ui.Red, 150); };
            quit.MouseLeave += delegate { Ui.AnimateColor(quitBrush, Ui.Gray, 150); };
            Ui.MakeClickable(quit, Quit, null);
            return g;
        }

        void Refresh()
        {
            Ui.AnimateColor(trackBrush, awake ? Ui.Green : Track, 220);
            knob.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(awake ? 20 : 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            });
            iconCircle.Background = awake
                ? new LinearGradientBrush(Ui.C("#3DDC6A"), Ui.C("#1E9E46"), 90)
                : new LinearGradientBrush(Ui.C("#48484A"), Ui.C("#2C2C2E"), 90);
            iconGlow.Opacity = awake ? 0.6 : 0;
            for (int i = 0; i < durationChips.Length; i++) durationChips[i].Selected = awake && i == durationIndex;
            screenChip.Selected = screenOn;
            startupChip.Selected = GetStartup();
            UpdateTimes();
            UpdateMode();
            UpdateTray();
        }

        void UpdateTimes()
        {
            var elapsed = DateTime.Now - since;
            var limit = Durations[durationIndex];
            if (!awake)
            {
                compactTime.Text = "";
                subtitle.Text = "Paused · your PC can sleep";
            }
            else if (limit.HasValue)
            {
                var left = limit.Value - elapsed;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                compactTime.Text = Format(left);
                subtitle.Text = "Awake · " + Format(left) + " left";
            }
            else
            {
                compactTime.Text = Format(elapsed);
                subtitle.Text = "Awake · on for " + Format(elapsed);
            }
        }

        // ---------- Music (any app) ----------

        MediaInfo media = new MediaInfo();
        byte[] shownArt;
        readonly SolidColorBrush accent = new SolidColorBrush(Ui.C("#1ED760"));
        DropShadowEffect artGlow;
        Image bigArt;
        TextBlock bigTitle, bigArtist, bigSource, posText, durText, musicVolumeText;
        LevelBar progress, musicVolume;
        Equalizer bigEq;
        Chip playChip;
        FrameworkElement nowPlaying, nothingPlaying;

        FrameworkElement BuildMusicPage()
        {
            var g = new Grid();

            // Now playing: glowing cover on the left; app, title, waveform, scrubber, controls and volume on the right.
            var playing = Ui.Columns(Ui.Auto, Ui.Star);
            artGlow = new DropShadowEffect { Color = MusicGreen, BlurRadius = 34, ShadowDepth = 0, Opacity = 0.75 };
            var artHolder = Ui.At(playing, new Border { Effect = artGlow, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(16), Background = Brushes.Black, Margin = new Thickness(0, 4, 0, 0) }, 0, 0);
            artHolder.Child = Ui.ArtBox(96, 16, out bigArt);

            var info = Ui.At(playing, new StackPanel { Margin = new Thickness(16, 0, 0, 0) }, 1, 0);
            bigSource = Ui.Text(10, Ui.B(Ui.Gray), true);
            info.Children.Add(bigSource);
            var titleRow = Ui.Columns(Ui.Star, Ui.Auto);
            titleRow.Margin = new Thickness(0, 2, 0, 0);
            var names = Ui.At(titleRow, new StackPanel(), 0, 0);
            bigTitle = Ui.Text(15, Brushes.White, true);
            bigArtist = Ui.Text(12.5, Ui.B(Ui.Gray), false);
            bigArtist.Margin = new Thickness(0, 2, 0, 0);
            names.Children.Add(bigTitle);
            names.Children.Add(bigArtist);
            bigEq = new Equalizer(accent, 5, 22, 3);
            bigEq.Root.Margin = new Thickness(10, 0, 2, 0);
            Ui.At(titleRow, bigEq.Root, 1, 0);
            info.Children.Add(titleRow);

            var bar = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            bar.Margin = new Thickness(0, 10, 0, 0);
            posText = Ui.At(bar, Ui.Text(10.5, Ui.B(Ui.Gray), false), 0, 0);
            progress = new LevelBar(4, Colors.White);
            progress.Root.Children[1].SetValue(Border.BackgroundProperty, accent);
            Ui.At(bar, Ui.Slider(progress, v =>
            {
                if (!media.Present || !media.CanSeek || media.Duration <= TimeSpan.Zero) return;
                var target = TimeSpan.FromSeconds(v * media.Duration.TotalSeconds);
                player.SeekTo(target);
                media.Position = target;
                media.UpdatedUtc = DateTime.UtcNow;
            }, new Thickness(8, 0, 8, 0)), 1, 0);
            durText = Ui.At(bar, Ui.Text(10.5, Ui.B(Ui.Gray), false), 2, 0);
            info.Children.Add(bar);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var prev = new Chip("", null, 34, 14, Colors.Transparent, Colors.White, Colors.Black, delegate { player.Send(MediaWatcher.Previous); });
            playChip = new Chip("", null, 38, 15, Colors.White, Colors.Black, delegate { player.Send(MediaWatcher.TogglePlay); });
            var next = new Chip("", null, 34, 14, Colors.Transparent, Colors.White, Colors.Black, delegate { player.Send(MediaWatcher.Next); });
            prev.Root.Width = next.Root.Width = 34;
            playChip.Root.Width = 38;
            prev.Root.Padding = next.Root.Padding = playChip.Root.Padding = new Thickness(0);
            playChip.Root.Margin = new Thickness(14, 0, 14, 0);
            playChip.Selected = true;
            controls.Children.Add(prev.Root);
            controls.Children.Add(playChip.Root);
            controls.Children.Add(next.Root);
            info.Children.Add(controls);

            var volume = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            volume.Margin = new Thickness(0, 6, 0, 0);
            Ui.At(volume, Ui.Glyph("", 12, Ui.B(Ui.Gray)), 0, 0);
            musicVolume = new LevelBar(4, Colors.White);
            Ui.At(volume, Ui.Slider(musicVolume, v => { audio.SetVolume(v); lastVolume = v; musicVolumeText.Text = Math.Round(v * 100) + ""; }, new Thickness(8, 0, 8, 0)), 1, 0);
            musicVolumeText = Ui.At(volume, Ui.Text(10.5, Ui.B(Ui.Gray), false), 2, 0);
            musicVolumeText.Width = 22;
            musicVolumeText.TextAlignment = TextAlignment.Right;
            info.Children.Add(volume);
            nowPlaying = playing;
            g.Children.Add(playing);

            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
            Image unused;
            var emptyArt = Ui.ArtBox(46, 12, out unused);
            emptyArt.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(emptyArt);
            var emptyText = Ui.Text("Nothing playing", 12.5, Ui.B(Ui.Gray), false);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.Margin = new Thickness(0, 10, 0, 4);
            empty.Children.Add(emptyText);
            var hint = Ui.Text("Spotify, YouTube, Apple Music, VLC — anything that plays shows up here", 11, Ui.B(Ui.Dim), false);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 0, 0, 12);
            empty.Children.Add(hint);
            var open = new Chip("", "Open Spotify", 30, 12, Colors.White, Colors.Black, delegate { Ui.Open("spotify:", "https://open.spotify.com"); });
            open.Root.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(open.Root);
            nothingPlaying = empty;
            g.Children.Add(empty);
            return g;
        }

        void OnMedia(MediaInfo info)
        {
            bool changedTrack = info.Present && info.Key != media.Key;
            bool changedPresence = info.Present != media.Present;
            media = info;

            if (info.Present)
            {
                bigTitle.Text = info.Title;
                bigArtist.Text = info.Artist;
                bigSource.Text = (info.Source ?? "").ToUpperInvariant();
                if (info.Art != shownArt)
                {
                    shownArt = info.Art;
                    var image = ToImage(info.Art);
                    smallArt.Source = bigArt.Source = image;
                    var color = image != null ? AccentFrom(image) : MusicGreen;
                    Ui.AnimateColor(accent, color, 500);
                    Ui.AnimateColor(artGlow, DropShadowEffect.ColorProperty, color, 500);
                    var washBrush = new RadialGradientBrush { Center = new Point(0.18, 0.55), GradientOrigin = new Point(0.18, 0.55), RadiusX = 0.75, RadiusY = 0.9 };
                    washBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, color.R, color.G, color.B), 0));
                    washBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
                    wash.Fill = washBrush;
                    if (view == ViewPeek && peekEq.Root.Visibility == Visibility.Visible) peekArt.Source = image;
                }
                playChip.SetGlyph(info.Playing ? "" : "");
            }
            bool playing = info.Present && info.Playing;
            musicEq.Active = peekEq.Active = bigEq.Active = playing;
            if (playing && !waveTimer.IsEnabled) waveTimer.Start();
            nowPlaying.Visibility = info.Present ? Visibility.Visible : Visibility.Collapsed;
            nothingPlaying.Visibility = info.Present ? Visibility.Collapsed : Visibility.Visible;
            UpdateProgress();
            UpdateMode();
            UpdateWash();

            if (changedTrack && popSongs) ShowPeek(ToImage(info.Art), null, null, MusicGreen, info.Title, info.Artist, true);
            if (changedPresence) RefitPage(TabMusic);
        }

        // Waveform frames. Runs while music plays, then until the bars settle.
        void OnWave()
        {
            double peak = audio.Peak();
            double target = peak < 0 || Program.Demo ? 0.75 : Math.Min(1, Math.Pow(peak, 0.6) * 1.15);
            level = Math.Max(target, level * 0.85);
            double t = clock.Elapsed.TotalSeconds;
            bool moving = musicEq.Tick(level, t);
            moving |= peekEq.Tick(level, t);
            moving |= bigEq.Tick(level, t);
            if (!musicEq.Active && !moving) waveTimer.Stop();
        }

        void UpdateProgress()
        {
            if (!media.Present || media.Duration <= TimeSpan.Zero)
            {
                progress.Set(0, false);
                posText.Text = durText.Text = "";
                return;
            }
            var pos = media.Position;
            if (media.Playing && media.UpdatedUtc.Year > 2000) pos += DateTime.UtcNow - media.UpdatedUtc;
            if (pos > media.Duration) pos = media.Duration;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            progress.Set(pos.TotalSeconds / media.Duration.TotalSeconds, false);
            posText.Text = Format(pos);
            durText.Text = Format(media.Duration);
        }

        static BitmapImage ToImage(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        // The most vivid colour in the cover, brightened - tints the waveform, progress and glow.
        static Color AccentFrom(BitmapSource source)
        {
            try
            {
                var small = new TransformedBitmap(source, new ScaleTransform(24.0 / source.PixelWidth, 24.0 / source.PixelHeight));
                var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = pixels.PixelWidth, h = pixels.PixelHeight;
                var px = new byte[w * h * 4];
                pixels.CopyPixels(px, w * 4, 0);
                double rs = 0, gs = 0, bs = 0, ws = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double sat = max == 0 ? 0 : (max - min) / max;
                    double weight = sat * sat * max + 0.002;
                    rs += r * weight; gs += g * weight; bs += b * weight; ws += weight;
                }
                double R = rs / ws, G = gs / ws, B = bs / ws;
                double top = Math.Max(R, Math.Max(G, B)), bottom = Math.Min(R, Math.Min(G, B));
                if (top < 0.02 || (top - bottom) / top < 0.12) return Ui.C("#E5E5EA");
                double k = 0.95 / top;
                R *= k; G *= k; B *= k;
                double avg = (R + G + B) / 3;
                Func<double, byte> boost = v => (byte)(255 * Math.Max(0, Math.Min(1, avg + (v - avg) * 1.3)));
                return Color.FromRgb(boost(R), boost(G), boost(B));
            }
            catch { return MusicGreen; }
        }

        // ---------- AI: ask from the notch, open an app, see which tools are working ----------

        TextBox askBox;
        StackPanel aiRunning;

        FrameworkElement BuildAiPage()
        {
            var page = new StackPanel();

            // A prompt that lands in Claude or ChatGPT (Enter sends to Claude).
            var ask = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto);
            Border askHost;
            askBox = Input("Ask anything…", false, out askHost);
            Ui.At(ask, askHost, 0, 0);
            var toClaude = new Chip("✳", null, 34, 15, Ui.C("#C2603F"), Ui.C("#E08A6A"), Colors.White, delegate { Ask("https://claude.ai/new?q="); });
            var toChatGpt = new Chip("", null, 34, 14, Ui.C("#0B7A5F"), Ui.C("#19C37D"), Colors.White, delegate { Ask("https://chatgpt.com/?q="); });
            ((TextBlock)((StackPanel)toClaude.Root.Child).Children[0]).FontFamily = Ui.Symbols;
            foreach (var c in new[] { toClaude, toChatGpt })
            {
                c.Root.Width = 34;
                c.Root.Padding = new Thickness(0);
                c.Root.Margin = new Thickness(6, 0, 0, 0);
            }
            toClaude.Root.ToolTip = "Ask Claude";
            toChatGpt.Root.ToolTip = "Ask ChatGPT";
            Ui.At(ask, toClaude.Root, 1, 0);
            Ui.At(ask, toChatGpt.Root, 2, 0);
            askBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Ask("https://claude.ai/new?q="); } };
            page.Children.Add(ask);

            var tiles = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(-4, 10, -4, 0) };
            tiles.Children.Add(AppTile("Claude", "✳", Ui.Symbols, new LinearGradientBrush(Ui.C("#E08A6A"), Ui.C("#C2603F"), 45), "https://claude.ai/new"));
            var gemini = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            gemini.GradientStops.Add(new GradientStop(Ui.C("#4285F4"), 0));
            gemini.GradientStops.Add(new GradientStop(Ui.C("#9B72CB"), 0.6));
            gemini.GradientStops.Add(new GradientStop(Ui.C("#D96570"), 1));
            tiles.Children.Add(AppTile("Gemini", "✦", Ui.Symbols, gemini, "https://gemini.google.com/app"));
            tiles.Children.Add(AppTile("ChatGPT", "", Ui.Icons, new LinearGradientBrush(Ui.C("#19C37D"), Ui.C("#0B7A5F"), 45), "https://chatgpt.com/"));
            page.Children.Add(tiles);

            var caption = Ui.Text("RUNNING ON THIS PC", 10.5, Ui.B(Ui.Dim), true);
            caption.Margin = new Thickness(2, 12, 0, 6);
            page.Children.Add(caption);
            aiRunning = new StackPanel();
            aiRunning.Children.Add(Ui.Text("Looking…", 12, Ui.B(Ui.Gray), false));
            page.Children.Add(aiRunning);
            return page;
        }

        void Ask(string baseUrl)
        {
            var prompt = askBox.Text.Trim();
            if (prompt.Length == 0) return;
            Ui.Open(baseUrl + Uri.EscapeDataString(prompt), null);
            askBox.Text = "";
            EndTyping();
            SetView(ViewCompact);
        }

        Border AppTile(string name, string glyph, FontFamily font, Brush badge, string url)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 9) };
            TextBlock glyphBlock;
            var badgeBox = Ui.Badge(28, 9, out glyphBlock);
            badgeBox.Background = badge;
            glyphBlock.Text = glyph;
            glyphBlock.FontSize = 14;
            glyphBlock.FontFamily = font;
            content.Children.Add(badgeBox);
            var label = Ui.Text(name, 12.5, Brushes.White, true);
            label.Margin = new Thickness(8, 0, 0, 0);
            content.Children.Add(label);
            var tile = Ui.Card(content, 14, delegate { Ui.Open(url, null); SetView(ViewCompact); });
            tile.Margin = new Thickness(4, 0, 4, 0);
            return tile;
        }

        // Who's running and who's actually working (busy CPU), read from this PC alone.
        void OnAiTools(List<AiTool> tools)
        {
            aiRunning.Children.Clear();
            if (tools.Count == 0)
            {
                aiRunning.Children.Add(Ui.Text("No AI apps or CLIs running right now", 12, Ui.B(Ui.Gray), false));
            }
            foreach (var t in tools)
            {
                var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
                row.Margin = new Thickness(2, 0, 2, 7);
                var dot = Ui.At(row, new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Ui.B(t.Working ? Ui.Green : Ui.Dim),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                }, 0, 0);
                if (t.Working)
                {
                    dot.Effect = new DropShadowEffect { Color = Ui.Green, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
                    dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.4, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                }
                Ui.At(row, Ui.Text(t.Name, 12.5, Brushes.White, true), 1, 0);
                Ui.At(row, Ui.Text(t.Working ? "Working · " + Math.Round(t.Cpu) + "% CPU" : "Idle", 11.5, Ui.B(t.Working ? Ui.Green : Ui.Gray), false), 2, 0);
                aiRunning.Children.Add(row);
            }
            RefitPage(TabAi);
        }

        // ---------- Calendar ----------

        DateTime calendarDay, calendarMonth;
        Grid calendarHost;

        FrameworkElement BuildCalendarPage()
        {
            calendarHost = new Grid();
            calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            RebuildCalendar();
            return calendarHost;
        }

        // Today on the left (with today's todos), a month grid on the right that only has as many rows as it needs.
        void RebuildCalendar()
        {
            var today = DateTime.Today;
            if (calendarDay != today && calendarDay != default(DateTime)) calendarMonth = new DateTime(today.Year, today.Month, 1);
            calendarDay = today;
            var culture = CultureInfo.CurrentCulture;
            calendarHost.Children.Clear();
            var g = Ui.At(calendarHost, Ui.Columns(new GridLength(112), Ui.Star), 0, 0);

            var left = Ui.At(g, new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 8, 0) }, 0, 0);
            left.Children.Add(Ui.Text(today.ToString("dddd", culture).ToUpper(culture), 11, Ui.B(Ui.Red), true));
            var big = Ui.Text(today.Day.ToString(culture), 46, Brushes.White, true);
            big.Margin = new Thickness(-2, -4, 0, -6);
            left.Children.Add(big);
            left.Children.Add(Ui.Text(today.ToString("MMMM yyyy", culture), 12, Ui.B(Ui.Gray), false));
            var due = todos.Where(t => !t.Done && t.Due.HasValue && t.Due.Value.Date == today).OrderBy(t => t.Due).Take(3).ToList();
            foreach (var t in due)
            {
                var item = Ui.Text(t.Due.Value.ToString("t", culture) + "  " + t.Text, 10.5, Ui.B("#D1D1D6"), false);
                item.Margin = new Thickness(0, 6, 0, 0);
                left.Children.Add(item);
            }

            var right = Ui.At(g, new StackPanel(), 1, 0);
            var head = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto);
            Ui.At(head, Ui.Text(calendarMonth.ToString("MMMM yyyy", culture), 13, Brushes.White, true), 0, 0);
            var prev = new Chip("", null, 24, 11, Colors.Transparent, Colors.White, Colors.Black, delegate { calendarMonth = calendarMonth.AddMonths(-1); RebuildCalendar(); RefitPage(TabCalendar); });
            var next = new Chip("", null, 24, 11, Colors.Transparent, Colors.White, Colors.Black, delegate { calendarMonth = calendarMonth.AddMonths(1); RebuildCalendar(); RefitPage(TabCalendar); });
            prev.Root.Width = next.Root.Width = 24;
            prev.Root.Padding = next.Root.Padding = new Thickness(0);
            Ui.At(head, prev.Root, 1, 0);
            Ui.At(head, next.Root, 2, 0);
            right.Children.Add(head);

            var firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
            var names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 6, 0, 2) };
            for (int i = 0; i < 7; i++)
            {
                var label = Ui.Text(culture.DateTimeFormat.ShortestDayNames[(firstDay + i) % 7], 10.5, Ui.B(Ui.Dim), true);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                names.Children.Add(label);
            }
            right.Children.Add(names);

            int offset = ((int)calendarMonth.DayOfWeek - firstDay + 7) % 7;
            int days = DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month);
            var grid = new UniformGrid { Columns = 7, Rows = (offset + days + 6) / 7 };
            for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
            for (int d = 1; d <= days; d++)
            {
                var date = new DateTime(calendarMonth.Year, calendarMonth.Month, d);
                bool isToday = date == today;
                bool hasTodo = todos.Any(t => !t.Done && t.Due.HasValue && t.Due.Value.Date == date);
                var cell = new Grid();
                var number = Ui.Text(d.ToString(culture), 11.5, isToday ? Brushes.White : Ui.B("#D1D1D6"), isToday);
                number.HorizontalAlignment = HorizontalAlignment.Center;
                cell.Children.Add(new Border
                {
                    Width = 24,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = isToday ? Ui.B(Ui.Red) : Brushes.Transparent,
                    Child = number
                });
                if (hasTodo && !isToday)
                    cell.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Ui.B(Ui.Orange), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center });
                grid.Children.Add(cell);
            }
            right.Children.Add(grid);
        }

        // ---------- Weather ----------

        StackPanel weatherHost;
        WeatherInfo lastWeather;

        FrameworkElement BuildWeatherPage()
        {
            weatherHost = new StackPanel();
            ShowWeatherMessage("Loading weather…", false);
            return weatherHost;
        }

        void ShowWeatherMessage(string message, bool retry)
        {
            weatherHost.Children.Clear();
            var text = Ui.Text(message, 12.5, Ui.B(Ui.Gray), false);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Margin = new Thickness(0, 18, 0, retry ? 12 : 18);
            weatherHost.Children.Add(text);
            if (!retry) return;
            var again = new Chip("", "Try again", 28, 12, Colors.White, Colors.Black, delegate
            {
                ShowWeatherMessage("Loading weather…", false);
                weather.Refresh(true);
            });
            again.Root.HorizontalAlignment = HorizontalAlignment.Center;
            again.Root.Margin = new Thickness(0, 0, 0, 6);
            weatherHost.Children.Add(again.Root);
        }

        void OnWeather(WeatherInfo w)
        {
            if (!w.Ok)
            {
                if (lastWeather == null) ShowWeatherMessage("Couldn't load the weather", true);
                RefitPage(TabWeather);
                return;
            }
            lastWeather = w;
            const string unit = "°";
            var culture = CultureInfo.CurrentCulture;
            weatherHost.Children.Clear();

            var top = Ui.Columns(Ui.Auto, Ui.Star);
            var symbol = Ui.At(top, new TextBlock
            {
                Text = WeatherService.Symbol(w.Code),
                FontFamily = Ui.Symbols,
                FontSize = 38,
                Foreground = Ui.B(w.Code == 113 ? "#FFD60A" : "#E5E5EA"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 14, 0)
            }, 0, 0);
            symbol.Effect = new DropShadowEffect { Color = w.Code == 113 ? Ui.C("#FFD60A") : Colors.White, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.35 };
            var main = Ui.At(top, new StackPanel { VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            var tempRow = new StackPanel { Orientation = Orientation.Horizontal };
            tempRow.Children.Add(Ui.Text(Math.Round(w.Temp) + unit, 32, Brushes.White, true));
            var hiLo = Ui.Text("H " + Math.Round(w.High) + unit + "   L " + Math.Round(w.Low) + unit + "   Feels " + Math.Round(w.FeelsLike) + unit, 12, Ui.B(Ui.Gray), false);
            hiLo.Margin = new Thickness(12, 8, 0, 0);
            tempRow.Children.Add(hiLo);
            main.Children.Add(tempRow);
            main.Children.Add(Ui.Text(w.Description + (string.IsNullOrEmpty(w.Place) ? "" : " · " + w.Place) +
                " · " + Math.Round(w.Humidity) + "% humidity · " + Math.Round(w.Wind) + (w.Metric ? " km/h" : " mph"), 11.5, Ui.B("#D1D1D6"), false));
            weatherHost.Children.Add(top);

            // The hours ahead.
            if (w.Hours.Count > 0)
            {
                var hours = new UniformGrid { Rows = 1, Columns = w.Hours.Count, Margin = new Thickness(-3, 12, -3, 0) };
                foreach (var h in w.Hours)
                {
                    var cell = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
                    var time = Ui.Text(h.Time.ToString("h tt", culture), 10.5, Ui.B(Ui.Gray), false);
                    time.HorizontalAlignment = HorizontalAlignment.Center;
                    var sym = new TextBlock { Text = WeatherService.Symbol(h.Code), FontFamily = Ui.Symbols, FontSize = 15, Foreground = Ui.B(h.Code == 113 ? "#FFD60A" : "#E5E5EA"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
                    var temp = Ui.Text(Math.Round(h.Temp) + unit, 12, Brushes.White, true);
                    temp.HorizontalAlignment = HorizontalAlignment.Center;
                    cell.Children.Add(time);
                    cell.Children.Add(sym);
                    cell.Children.Add(temp);
                    var card = Ui.Card(cell, 12, null);
                    card.Margin = new Thickness(3, 0, 3, 0);
                    hours.Children.Add(card);
                }
                weatherHost.Children.Add(hours);
            }

            // The next days.
            foreach (var d in w.Days)
            {
                var row = Ui.Columns(new GridLength(90), Ui.Auto, Ui.Star, Ui.Auto);
                row.Margin = new Thickness(4, 8, 4, 0);
                Ui.At(row, Ui.Text(d.Date == DateTime.Today ? "Today" : d.Date.ToString("dddd", culture), 12, Brushes.White, true), 0, 0);
                Ui.At(row, new TextBlock { Text = WeatherService.Symbol(d.Code), FontFamily = Ui.Symbols, FontSize = 14, Foreground = Ui.B(d.Code == 113 ? "#FFD60A" : "#E5E5EA"), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
                Ui.At(row, Ui.Text(Math.Round(d.High) + unit + "  /  " + Math.Round(d.Low) + unit, 12, Ui.B("#D1D1D6"), false), 3, 0);
                weatherHost.Children.Add(row);
            }
            RefitPage(TabWeather);
        }

        // ---------- System monitor ----------

        LevelBar cpuBar, gpuBar, ramBar, diskBar;
        TextBlock cpuText, gpuText, ramText, diskText, diskLabel, netText, ioText;
        FrameworkElement gpuRow;
        Polyline cpuChart;
        readonly List<double> cpuHistory = new List<double>();
        const int CpuHistoryLength = 60;

        FrameworkElement BuildSystemPage()
        {
            var g = new StackPanel();

            // CPU over the last minute.
            var chart = new Grid { Height = 38, Margin = new Thickness(0, 0, 0, 10), ClipToBounds = true };
            chart.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Ui.B(Ui.Surface) });
            cpuChart = new Polyline { Stroke = Ui.B(Ui.Green), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(8, 4, 8, 4) };
            chart.Children.Add(cpuChart);
            var chartLabel = Ui.Text("CPU · last minute", 10, Ui.B(Ui.Gray), false);
            chartLabel.Margin = new Thickness(10, 4, 0, 0);
            chartLabel.VerticalAlignment = VerticalAlignment.Top;
            chart.Children.Add(chartLabel);
            g.Children.Add(chart);

            g.Children.Add(StatRow("", "CPU", out cpuBar, out cpuText));
            g.Children.Add(gpuRow = StatRow("", "GPU", out gpuBar, out gpuText));
            g.Children.Add(StatRow("", "Memory", out ramBar, out ramText));
            var disk = StatRow("", "Disk", out diskBar, out diskText);
            diskLabel = (TextBlock)((Grid)disk).Children[1];
            g.Children.Add(disk);
            g.Children.Add(InfoRow("", "Network", out netText));
            g.Children.Add(InfoRow("", "Disk I/O", out ioText));
            return g;
        }

        static Grid InfoRow(string glyph, string name, out TextBlock value)
        {
            var row = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star);
            row.Margin = new Thickness(0, 2, 0, 6);
            var icon = Ui.At(row, Ui.Glyph(glyph, 13, Ui.B(Ui.Gray)), 0, 0);
            icon.Width = 22;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            var label = Ui.At(row, Ui.Text(name, 12, Ui.B("#D1D1D6"), true), 1, 0);
            label.Width = 62;
            value = Ui.At(row, Ui.Text(12, Brushes.White, false), 2, 0);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            return row;
        }

        static Grid StatRow(string glyph, string name, out LevelBar bar, out TextBlock value)
        {
            var row = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star, Ui.Auto);
            row.Margin = new Thickness(0, 0, 0, 10);
            var icon = Ui.At(row, Ui.Glyph(glyph, 13, Ui.B(Ui.Gray)), 0, 0);
            icon.Width = 22;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            var label = Ui.At(row, Ui.Text(name, 12, Ui.B("#D1D1D6"), true), 1, 0);
            label.Width = 62;
            bar = new LevelBar(6, Ui.Green);
            Ui.At(row, bar.Root, 2, 0);
            value = Ui.At(row, Ui.Text(12, Brushes.White, false), 3, 0);
            value.Width = 104;
            value.TextAlignment = TextAlignment.Right;
            return row;
        }

        void OnStats(SystemSnapshot s)
        {
            SetStat(cpuBar, cpuText, s.Cpu, Math.Round(s.Cpu) + "%");
            gpuRow.Visibility = s.HasGpu ? Visibility.Visible : Visibility.Collapsed;
            SetStat(gpuBar, gpuText, s.Gpu, Math.Round(s.Gpu) + "%");
            SetStat(ramBar, ramText, s.Ram, string.Format(CultureInfo.CurrentCulture, "{0:0.0} / {1:0} GB", s.RamUsedGb, s.RamTotalGb));
            diskLabel.Text = "Disk " + s.Drive;
            SetStat(diskBar, diskText, s.Disk, string.Format(CultureInfo.CurrentCulture, "{0:0} GB free", s.DiskFreeGb));
            netText.Text = "↓ " + Speed(s.DownBps) + "    ↑ " + Speed(s.UpBps);
            ioText.Text = "read " + Bytes(s.DiskReadBps) + "/s    write " + Bytes(s.DiskWriteBps) + "/s";

            cpuHistory.Add(s.Cpu);
            if (cpuHistory.Count > CpuHistoryLength) cpuHistory.RemoveAt(0);
            double w = ContentW - 16, h = 30;
            var points = new PointCollection();
            for (int i = 0; i < cpuHistory.Count; i++)
                points.Add(new Point(w * (i + CpuHistoryLength - cpuHistory.Count) / (CpuHistoryLength - 1), h - h * cpuHistory[i] / 100));
            cpuChart.Points = points;
            Ui.AnimateColor((SolidColorBrush)cpuChart.Stroke, s.Cpu > 90 ? Ui.Red : s.Cpu > 75 ? Ui.Orange : Ui.Green, 300);
            RefitPage(TabSystem);
        }

        // Bars turn amber past 75% and red past 90%.
        static void SetStat(LevelBar bar, TextBlock text, double percent, string label)
        {
            bar.Set(percent / 100, true);
            Ui.AnimateColor(bar.Fill, percent > 90 ? Ui.Red : percent > 75 ? Ui.Orange : Ui.Green, 300);
            text.Text = label;
        }

        static string Speed(double bytesPerSecond)
        {
            double bits = bytesPerSecond * 8;
            if (bits >= 1e9) return (bits / 1e9).ToString("0.0", CultureInfo.CurrentCulture) + " Gb/s";
            if (bits >= 1e6) return (bits / 1e6).ToString("0.0", CultureInfo.CurrentCulture) + " Mb/s";
            return (bits / 1e3).ToString("0", CultureInfo.CurrentCulture) + " kb/s";
        }

        static string Bytes(double bytes)
        {
            if (bytes >= 1073741824) return (bytes / 1073741824).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            if (bytes >= 1048576) return (bytes / 1048576).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            return (bytes / 1024).ToString("0", CultureInfo.CurrentCulture) + " KB";
        }

        // ---------- File shelf ----------

        readonly List<string> shelf = new List<string>();
        Grid shelfHost;
        const int ShelfMax = 9;

        FrameworkElement BuildShelfPage()
        {
            shelfHost = new Grid();
            RebuildShelf();
            return shelfHost;
        }

        void AddToShelf(string path)
        {
            shelf.Remove(path);
            shelf.Add(path);
            while (shelf.Count > ShelfMax) shelf.RemoveAt(0);
            RebuildShelf();
            RefitPage(TabShelf);
        }

        // Files dropped on the island (and finished downloads) are parked here by reference.
        void RebuildShelf()
        {
            shelfHost.Children.Clear();
            if (shelf.Count == 0)
            {
                var drop = new Grid { Height = 96 };
                drop.Children.Add(new Rectangle
                {
                    RadiusX = 18,
                    RadiusY = 18,
                    Stroke = Ui.B("#55FFFFFF"),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                });
                var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                hint.Children.Add(Ui.Glyph("", 20, Ui.B(Ui.Gray)));
                var text = Ui.Text("Drop files on the island to keep them here", 12, Ui.B(Ui.Gray), false);
                text.Margin = new Thickness(0, 8, 0, 0);
                hint.Children.Add(text);
                drop.Children.Add(hint);
                shelfHost.Children.Add(drop);
                return;
            }

            var panel = new StackPanel();
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 0, -4, 0) };
            foreach (var path in shelf.ToList())
            {
                string file = path;
                var content = Ui.Columns(Ui.Auto, Ui.Star);
                content.Margin = new Thickness(8, 7, 8, 7);
                var icon = FileIcon(file);
                if (icon != null) Ui.At(content, new Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) }, 0, 0);
                else Ui.At(content, Ui.Glyph(Directory.Exists(file) ? "" : "", 18, Brushes.White), 0, 0).Margin = new Thickness(0, 0, 8, 0);
                var name = Ui.At(content, Ui.Text(System.IO.Path.GetFileName(file.TrimEnd('\\')), 11.5, Brushes.White, false), 1, 0);
                name.ToolTip = file;
                var card = Ui.Card(content, 12, delegate { Ui.Open(file, null); });
                card.Margin = new Thickness(4, 0, 4, 8);
                card.ToolTip = "Click to open, right-click to remove";
                card.MouseRightButtonUp += (s, e) => { e.Handled = true; shelf.Remove(file); RebuildShelf(); RefitPage(TabShelf); };
                grid.Children.Add(card);
            }
            panel.Children.Add(grid);
            var clear = new Chip("", "Clear shelf", 26, 11.5, Colors.White, Colors.Black, delegate { shelf.Clear(); RebuildShelf(); RefitPage(TabShelf); });
            clear.Root.HorizontalAlignment = HorizontalAlignment.Right;
            panel.Children.Add(clear.Root);
            shelfHost.Children.Add(panel);
        }

        static ImageSource FileIcon(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var icon = Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
            }
            catch { return null; }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
            if (e.Effects == DragDropEffects.None) return;
            collapseTimer.Stop();
            if (view != ViewExpanded) SetView(ViewExpanded);
            SetTab(TabShelf);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            foreach (var f in files) AddToShelf(f);
        }
    }
}
