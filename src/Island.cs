// The island window: shape, morphing between views, compact modes, pop-ups, gestures, menu, settings.
// The dashboard pages live in Pages.cs and Pages.Tools.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Lumi
{
    partial class IslandWindow : Window
    {
        // Views: the resting pill, a short pop-up banner, the open dashboard, and the volume/brightness HUD.
        const int ViewCompact = 0, ViewPeek = 1, ViewExpanded = 2, ViewHud = 3;
        // What the resting pill shows, most important first: teleprompter, focus, download, music, Always On.
        const int ModeIdle = 0, ModeAwake = 1, ModeMusic = 2, ModeFocus = 3, ModeDownload = 4, ModePrompter = 5;

        const double CH = 34, IdleW = 168, AwakeW = 206, MusicW = 214, FocusW = 206, DownloadW = 246, PrompterW = 540, PrivacyPad = 14;
        const double PeekW = 360, PeekH = 74, HudW = 300, EW = 404;
        const double ContentW = EW - 36, ExpandedTop = 12 + 28 + 14, ExpandedBottom = 12 + 5 + 14;
        const int HotkeyId = 0xA11;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = Program.Name, OldRunName = "AgentsAlwaysOn";

        static readonly string[] ThemeNames = { "Obsidian", "Graphite", "Slate", "Nord", "Evergreen", "Espresso", "Plum" };
        static readonly string[] ThemeColors = { "#000000", "#1C1C1E", "#1B2230", "#2E3440", "#0F1F17", "#22180F", "#22142A" };
        static readonly double[] Opacities = { 1, 0.9, 0.8, 0.7 };
        static readonly string SettingsFile = Store.PathOf("settings.ini");
        static readonly string OldSettingsFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsAlwaysOn", "settings.ini");

        readonly Spring width = new Spring(IdleW), height = new Spring(CH);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly DispatcherTimer hoverTimer, collapseTimer, peekTimer, hudTimer, tick, waveTimer, volumeTimer, clipTimer;
        readonly MediaWatcher player = new MediaWatcher();
        readonly NotificationWatcher notifications = new NotificationWatcher();
        readonly SystemStats stats = new SystemStats();
        readonly WeatherService weather = new WeatherService();
        readonly BrightnessWatcher brightness = new BrightnessWatcher();
        readonly BluetoothWatcher bluetooth = new BluetoothWatcher();
        readonly AiToolsMonitor aiTools = new AiToolsMonitor();
        readonly DevServerScanner devServers = new DevServerScanner();
        readonly DownloadWatcher downloads = new DownloadWatcher();
        readonly AudioDevice audio = new AudioDevice();
        double lastFrame, level, lastVolume = -1;
        bool lastMuted, animating, cleanedUp, camOn, typing, downloading;
        int view = ViewCompact, mode = -1;
        IntPtr hwnd;

        // Shape.
        Grid island, clipHost, inner;
        System.Windows.Shapes.Path shape;
        Rectangle wash;

        // Layers.
        Grid compact, awakeLayer, musicLayer, focusLayer, downloadLayer, prompterLayer, peek, expanded, hud;
        ScaleTransform expandedScale;
        Ellipse camDot;

        // Compact, peek and HUD content.
        TextBlock compactTime, downloadText, peekTitle, peekSubtitle, peekGlyph, hudGlyph, hudValue;
        Image smallArt, peekArt, peekLogo;
        Grid peekArtBox;
        Border peekBadge;
        Equalizer musicEq, peekEq;
        Grid peekRing;
        System.Windows.Shapes.Path peekRingArc;
        TextBlock peekRingText;
        LevelBar hudBar;
        Action<double> hudSetter;

        // Settings.
        bool awake, screenOn = true, hidden, notch = true, pinned, hoverExpand = true, followMouse;
        bool popSongs = true, popHud = true, popClipboard = true, popNotifications = true, popBluetooth = true, popDownloads = true;
        int theme, opacityIndex, durationIndex;
        DateTime since;
        double savedLeft = double.NaN, savedTop = double.NaN;
        WinForms.Screen lastScreen;

        WinForms.NotifyIcon tray;
        WinForms.ContextMenuStrip menu;
        Drawing.Icon iconOn, iconOff;

        public IslandWindow()
        {
            Title = Program.Name;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = EW + 28 + 160;
            Height = 470;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding = true;
            AllowDrop = true;

            if (!Program.Demo)
            {
                MigrateFromOldName();
                LoadSettings();
            }
            Content = BuildUi();
            ApplyStyle();

            hoverTimer = Timer(180, delegate
            {
                hoverTimer.Stop();
                if (island.IsMouseOver && hoverExpand && !(view == ViewHud && hud.IsMouseOver)) SetView(ViewExpanded);
            });
            collapseTimer = Timer(450, delegate
            {
                collapseTimer.Stop();
                if (!island.IsMouseOver && view == ViewExpanded && !pinned && !typing) SetView(ViewCompact);
            });
            peekTimer = Timer(3500, delegate { peekTimer.Stop(); if (view == ViewPeek) SetView(ViewCompact); });
            hudTimer = Timer(1600, delegate
            {
                hudTimer.Stop();
                if (view != ViewHud) return;
                if (hud.IsMouseOver) hudTimer.Start();
                else SetView(ViewCompact);
            });
            tick = Timer(1000, delegate { OnTick(); });
            waveTimer = Timer(33, delegate { OnWave(); });
            volumeTimer = Timer(150, delegate { OnVolumePoll(); });
            clipTimer = Timer(200, delegate { clipTimer.Stop(); OnClipboard(); });
            tick.Start();
            if (!Program.Demo) volumeTimer.Start();

            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                // Tool window: no taskbar button or Alt+Tab entry. No-activate: clicking it never steals focus.
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                HwndSource.FromHwnd(hwnd).AddHook(WndProc);
                if (!Program.Demo)
                {
                    Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x44 /* D */);
                    Native.AddClipboardFormatListener(hwnd);
                }
                PlaceWindow();
            };
            // Typing ends when you click anywhere else.
            Deactivated += delegate
            {
                EndTyping();
                if (!island.IsMouseOver) collapseTimer.Start();
            };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            iconOn = MakeTrayIcon(true);
            iconOff = MakeTrayIcon(false);
            BuildMenu();
            SetTab(tab);
            SetAwake(true, 0, false);
            UpdateClock();
            UpdatePin();
            ApplyGeometry();

            player.Changed += info => Dispatcher.BeginInvoke(new Action(() => OnMedia(info)));
            notifications.Arrived += n => Dispatcher.BeginInvoke(new Action(() => OnNotice(n)));
            stats.Changed += s => Dispatcher.BeginInvoke(new Action(() => OnStats(s)));
            weather.Changed += w => Dispatcher.BeginInvoke(new Action(() => OnWeather(w)));
            brightness.Changed += b => Dispatcher.BeginInvoke(new Action(() => OnBrightness(b)));
            bluetooth.Changed += d => Dispatcher.BeginInvoke(new Action(() => OnDevice(d)));
            bluetooth.Devices += list => Dispatcher.BeginInvoke(new Action(() => OnDevices(list)));
            aiTools.Changed += list => Dispatcher.BeginInvoke(new Action(() => OnAiTools(list)));
            devServers.Changed += list => Dispatcher.BeginInvoke(new Action(() => OnDevServers(list)));
            downloads.Progress += p => Dispatcher.BeginInvoke(new Action(() => OnDownload(p)));
            downloads.Finished += path => Dispatcher.BeginInvoke(new Action(() => OnDownloadFinished(path)));
            if (Program.Demo) return;
            player.Start();
            notifications.Start();
            bluetooth.Start();
            stats.Start();
            brightness.Start();
            aiTools.Start();
            devServers.Start();
            downloads.Start();
        }

        DispatcherTimer Timer(int ms, EventHandler onTick)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += onTick;
            return t;
        }

        // ---------- Building ----------

        UIElement BuildUi()
        {
            var root = new Grid();
            island = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, AllowDrop = true };
            shape = new System.Windows.Shapes.Path
            {
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.55 }
            };
            island.Children.Add(shape);

            clipHost = new Grid();
            wash = new Rectangle { Opacity = 0, IsHitTestVisible = false };
            clipHost.Children.Add(wash);
            inner = new Grid();
            clipHost.Children.Add(inner);
            island.Children.Add(clipHost);
            root.Children.Add(island);

            compact = new Grid { Height = CH, VerticalAlignment = VerticalAlignment.Top };
            awakeLayer = BuildAwakeCompact();
            musicLayer = BuildMusicCompact();
            focusLayer = BuildFocusCompact();
            downloadLayer = BuildDownloadCompact();
            prompterLayer = BuildPrompterCompact();
            foreach (var layer in new[] { awakeLayer, musicLayer, focusLayer, downloadLayer, prompterLayer })
            {
                Ui.Hidden(layer);
                compact.Children.Add(layer);
            }

            // Green dot while an app is using the camera, like iOS.
            camDot = PrivacyDot(Ui.Green);
            camDot.HorizontalAlignment = HorizontalAlignment.Right;
            camDot.VerticalAlignment = VerticalAlignment.Center;
            camDot.Margin = new Thickness(0, 0, 6, 0);
            compact.Children.Add(camDot);

            peek = BuildPeek();
            hud = BuildHud();
            expanded = BuildExpanded();
            Ui.Hidden(compact);
            Ui.Hidden(peek);
            Ui.Hidden(hud);
            Ui.Hidden(expanded);
            inner.Children.Add(compact);
            inner.Children.Add(peek);
            inner.Children.Add(hud);
            inner.Children.Add(expanded);

            island.MouseEnter += delegate { collapseTimer.Stop(); if (view != ViewExpanded) hoverTimer.Start(); };
            island.MouseLeave += delegate { hoverTimer.Stop(); collapseTimer.Start(); };
            island.MouseLeftButtonDown += OnIslandMouseDown;
            island.MouseRightButtonUp += (s, e) => { e.Handled = true; ShowMenu(); };
            // Scroll down on the pill to open it; scroll on the open island to flip between apps.
            island.MouseWheel += (s, e) =>
            {
                e.Handled = true;
                if (view != ViewExpanded)
                {
                    if (e.Delta < 0) SetView(ViewExpanded);
                    return;
                }
                if (!launcherOpen) Flip(e.Delta < 0 ? 1 : -1);
            };
            island.DragEnter += OnDragEnter;
            island.DragOver += OnDragOver;
            island.DragLeave += delegate { collapseTimer.Start(); };
            island.Drop += OnDrop;
            return root;
        }

        void Flip(int direction)
        {
            SetTab((tab + direction + TabCount) % TabCount);
        }

        static Ellipse PrivacyDot(Color color)
        {
            return new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(color),
                Effect = new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 },
                Margin = new Thickness(2, 0, 2, 0),
                Visibility = Visibility.Collapsed
            };
        }

        // Compact Always On: green power glyph on the left, timer on the right.
        Grid BuildAwakeCompact()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = AwakeW;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var glyph = Ui.Glyph("", 14, Ui.B(Ui.Green));
            glyph.Effect = new DropShadowEffect { Color = Ui.Green, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 };
            glyph.Margin = new Thickness(14, 0, 0, 0);
            glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            Ui.At(g, glyph, 0, 0);
            compactTime = Ui.At(g, Ui.Text(12.5, Ui.B(Ui.Green), true), 2, 0);
            compactTime.Margin = new Thickness(0, 0, 15, 0);
            return g;
        }

        // Compact music: cover on the left, live waveform on the right, nothing in between.
        Grid BuildMusicCompact()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = MusicW;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var art = Ui.At(g, Ui.ArtBox(22, 6, out smallArt), 0, 0);
            art.Margin = new Thickness(9, 0, 0, 0);
            art.VerticalAlignment = VerticalAlignment.Center;
            musicEq = new Equalizer(accent, 5, 15, 2.6);
            musicEq.Root.Margin = new Thickness(0, 0, 13, 0);
            Ui.At(g, musicEq.Root, 2, 0);
            return g;
        }

        // Compact download: a pulsing arrow on the left, speed and size on the right.
        Grid BuildDownloadCompact()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = DownloadW;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var arrow = Ui.At(g, Ui.Glyph("", 14, Ui.B("#0A84FF")), 0, 0);
            arrow.Margin = new Thickness(14, 0, 0, 0);
            arrow.Effect = new DropShadowEffect { Color = Ui.C("#0A84FF"), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
            arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            downloadText = Ui.At(g, Ui.Text(12, Brushes.White, true), 2, 0);
            downloadText.Margin = new Thickness(0, 0, 15, 0);
            return g;
        }

        // Peek: a short-lived banner for a new song, notification, copy, reminder, or Always On change.
        Grid BuildPeek()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = PeekW;
            g.Height = PeekH;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Top;

            var left = Ui.At(g, new Grid { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            peekArtBox = Ui.ArtBox(44, 11, out peekArt);
            peekBadge = Ui.Badge(44, 13, out peekGlyph);
            peekLogo = new Image { Width = 30, Height = 30 };
            RenderOptions.SetBitmapScalingMode(peekLogo, BitmapScalingMode.HighQuality);
            var badgeContent = new Grid();
            peekBadge.Child = badgeContent;
            badgeContent.Children.Add(peekGlyph);
            badgeContent.Children.Add(peekLogo);
            left.Children.Add(peekArtBox);
            left.Children.Add(peekBadge);

            var text = Ui.At(g, new StackPanel { VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            peekTitle = Ui.Text(14, Brushes.White, true);
            peekSubtitle = Ui.Text(12, Ui.B(Ui.Gray), false);
            peekSubtitle.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(peekTitle);
            text.Children.Add(peekSubtitle);

            peekEq = new Equalizer(accent, 5, 20, 3);
            peekEq.Root.Margin = new Thickness(10, 0, 18, 0);
            Ui.At(g, peekEq.Root, 2, 0);

            // Battery ring for headphones, like the iPhone battery widget.
            peekRing = Ui.At(g, new Grid { Width = 40, Height = 40, Margin = new Thickness(10, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center }, 2, 0);
            peekRing.Children.Add(new Ellipse { Stroke = Ui.B("#33FFFFFF"), StrokeThickness = 3.5 });
            peekRingArc = new System.Windows.Shapes.Path
            {
                StrokeThickness = 3.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = new SolidColorBrush(Ui.Green)
            };
            peekRing.Children.Add(peekRingArc);
            peekRingText = Ui.Text(11.5, Brushes.White, true);
            peekRingText.HorizontalAlignment = HorizontalAlignment.Center;
            peekRing.Children.Add(peekRingText);
            return g;
        }

        // Clockwise arc from 12 o'clock covering `fraction` of the ring.
        static Geometry RingArc(double size, double thickness, double fraction)
        {
            fraction = Math.Max(0.001, Math.Min(0.9999, fraction));
            double r = (size - thickness) / 2, c = size / 2, angle = fraction * 2 * Math.PI;
            var figure = new PathFigure { StartPoint = new Point(c, c - r) };
            figure.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(figure);
            return g;
        }

        // HUD: the notch widens into a volume / brightness slider you can also drag.
        Grid BuildHud()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            g.Width = HudW;
            g.Height = CH;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Top;
            hudGlyph = Ui.At(g, Ui.Glyph("", 15, Brushes.White), 0, 0);
            hudGlyph.Margin = new Thickness(16, 0, 12, 0);
            hudBar = new LevelBar(6, Colors.White);
            Ui.At(g, Ui.Slider(hudBar, v =>
            {
                hudValue.Text = Math.Round(v * 100) + "";
                if (hudSetter != null) hudSetter(v);
                hudTimer.Stop();
                hudTimer.Start();
            }, new Thickness(0)), 1, 0);
            Ui.At(g, hudValue = Ui.Text(12, Ui.B(Ui.Gray), true), 2, 0);
            hudValue.Margin = new Thickness(12, 0, 16, 0);
            hudValue.Width = 30;
            hudValue.TextAlignment = TextAlignment.Right;
            return g;
        }

        // ---------- Typing ----------

        void SetActivatable(bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, on ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE);
        }

        void BeginTyping()
        {
            if (typing) return;
            typing = true;
            SetActivatable(true);
            Activate();
        }

        void EndTyping()
        {
            if (!typing) return;
            typing = false;
            Keyboard.ClearFocus();
            SetActivatable(false);
        }

        // ---------- Always On ----------

        void SetAwake(bool on, int index, bool announce)
        {
            awake = on;
            durationIndex = index;
            since = DateTime.Now;
            ApplyPower();
            Refresh();
            if (announce)
                ShowPeek(null, null, on ? "" : "", on ? Ui.Green : Ui.C("#3A3A3C"),
                    on ? "Always On" : "Always On paused", on ? "Keeping your PC awake" : "Your PC can sleep normally", false);
        }

        void ApplyPower()
        {
            uint flags = Native.ES_CONTINUOUS;
            if (awake)
            {
                flags |= Native.ES_SYSTEM_REQUIRED;
                if (screenOn) flags |= Native.ES_DISPLAY_REQUIRED;
            }
            Native.SetThreadExecutionState(flags);
        }

        void OnTick()
        {
            var limit = Durations[durationIndex];
            if (awake && limit.HasValue && DateTime.Now - since >= limit.Value)
                SetAwake(false, durationIndex, true);
            else
                UpdateTimes();
            UpdateProgress();
            UpdateClock();
            UpdatePrivacy();
            FocusTick();
            TodoTick();
            if (followMouse && notch) FollowMouse();
            if (calendarDay != DateTime.Today) RebuildCalendar();
        }

        static string Format(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        void UpdateClock()
        {
            clockText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
            var power = WinForms.SystemInformation.PowerStatus;
            if ((power.BatteryChargeStatus & WinForms.BatteryChargeStatus.NoSystemBattery) != 0 || power.BatteryLifePercent > 1)
                batteryText.Text = "";
            else
                batteryText.Text = (power.PowerLineStatus == WinForms.PowerLineStatus.Online ? "⚡ " : "") +
                    Math.Round(power.BatteryLifePercent * 100) + "%";
        }

        void UpdatePrivacy()
        {
            bool cam = Privacy.InUse("webcam");
            if (cam == camOn) return;
            camOn = cam;
            camDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
            if (view == ViewCompact) SetView(ViewCompact, true);
        }

        // ---------- Pop-ups ----------

        void ShowPeek(ImageSource art, ImageSource logo, string glyph, Color badge, string title, string subtitle, bool eq)
        {
            if (view == ViewExpanded || hidden || prompting) return;
            peekArtBox.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
            peekBadge.Visibility = art != null ? Visibility.Collapsed : Visibility.Visible;
            peekArt.Source = art;
            peekLogo.Source = logo;
            peekLogo.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
            peekGlyph.Visibility = logo != null ? Visibility.Collapsed : Visibility.Visible;
            peekGlyph.Text = glyph ?? "";
            peekBadge.Background = new SolidColorBrush(badge);
            peekTitle.Text = title ?? "";
            peekSubtitle.Text = subtitle ?? "";
            peekSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            peekEq.Root.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;
            peekRing.Visibility = Visibility.Collapsed;
            if (view == ViewHud) hudTimer.Stop();
            SetView(ViewPeek);
            peekTimer.Stop();
            peekTimer.Start();
            UpdateWash();
        }

        // AirPods-style card: headphones badge, device name, "Connected", and a battery ring.
        void OnDevice(DeviceEvent e)
        {
            if (!popBluetooth) return;
            ShowPeek(null, null, "", e.Connected ? Ui.C("#2C2C2E") : Ui.C("#1C1C1E"), e.Name, e.Connected ? "Connected" : "Disconnected", false);
            if (e.Connected && e.Battery >= 0)
            {
                var color = e.Battery <= 20 ? Ui.Red : Ui.Green;
                peekRingArc.Data = RingArc(40, 3.5, e.Battery / 100.0);
                ((SolidColorBrush)peekRingArc.Stroke).Color = color;
                peekRingText.Text = e.Battery.ToString(CultureInfo.CurrentCulture);
                peekRing.Visibility = Visibility.Visible;
            }
            peekTimer.Interval = TimeSpan.FromSeconds(4);
            peekTimer.Stop();
            peekTimer.Start();
        }

        void ShowHud(string glyph, double value, Action<double> setter)
        {
            if (!popHud || view == ViewExpanded || hidden || prompting) return;
            hudGlyph.Text = glyph;
            hudBar.Set(value, view == ViewHud);
            hudValue.Text = Math.Round(value * 100) + "";
            hudSetter = setter;
            SetView(ViewHud);
            hudTimer.Stop();
            hudTimer.Start();
        }

        void OnVolumePoll()
        {
            double volume;
            bool muted;
            if (!audio.TryGetVolume(out volume, out muted)) return;
            bool changed = lastVolume >= 0 && (Math.Abs(volume - lastVolume) > 0.004 || muted != lastMuted);
            bool first = lastVolume < 0;
            lastVolume = volume;
            lastMuted = muted;
            if (changed || first)
            {
                musicVolume.Set(muted ? 0 : volume, false);
                musicVolumeText.Text = Math.Round((muted ? 0 : volume) * 100) + "";
            }
            if (!changed) return;
            string glyph = muted || volume == 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";
            ShowHud(glyph, muted ? 0 : volume, v => { audio.SetVolume(v); lastVolume = v; });
        }

        void OnBrightness(int value)
        {
            ShowHud("", value / 100.0, v => BrightnessWatcher.Set((int)Math.Round(v * 100)));
        }

        void OnNotice(Notice n)
        {
            if (!popNotifications) return;
            var logo = ToImage(n.Logo);
            string title = string.IsNullOrEmpty(n.Title) ? n.App : n.Title;
            string body = string.IsNullOrEmpty(n.Body) ? n.App : n.App + " · " + n.Body;
            ShowPeek(null, logo, "", Ui.C("#2C2C2E"), title, body, false);
            peekTimer.Interval = TimeSpan.FromSeconds(5);
            peekTimer.Stop();
            peekTimer.Start();
        }

        // Every copy goes into the clipboard history (memory only); a pop-up describes it.
        void OnClipboard()
        {
            if (clock.Elapsed.TotalSeconds < 3) return;
            try
            {
                // Password managers mark secrets so clipboard viewers leave them alone.
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Clipboard.ContainsData("Clipboard Viewer Ignore")) return;
                var entry = RecordClip();
                if (ignoreNextClip) { ignoreNextClip = false; return; }
                if (entry == null || !popClipboard) return;
                peekTimer.Interval = TimeSpan.FromSeconds(2.5);
                var blue = Ui.C("#0A84FF");
                var flat = entry.Text == null ? "" : string.Join(" ", entry.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                if (entry.Kind == "Images") ShowPeek(entry.Image, null, null, blue, "Copied image", entry.Text, false);
                else if (entry.Kind == "Files") ShowPeek(null, null, "", blue, entry.Files.Count == 1 ? "Copied file" : "Copied " + entry.Files.Count + " files", entry.Text, false);
                else if (entry.Kind == "Colours") ShowPeek(null, null, "", entry.Swatch, "Copied colour", entry.Text, false);
                else if (entry.Kind == "Links") ShowPeek(null, null, "", blue, "Copied link", CleanLink(entry.Text), false);
                else if (System.Text.RegularExpressions.Regex.IsMatch(flat, @"^\d{4,8}$"))
                    ShowPeek(null, null, "", Ui.Green, flat, "Verification code copied", false);
                else ShowPeek(null, null, "", blue, "Copied", flat.Length > 90 ? flat.Substring(0, 90) + "…" : flat, false);
            }
            catch { }
        }

        // Downloads: progress in the pill while it runs, then the file lands on the shelf.
        void OnDownload(DownloadProgress p)
        {
            downloading = p.Active && popDownloads;
            if (p.Active) downloadText.Text = (p.Speed > 0 ? Bytes(p.Speed) + "/s · " : "") + Bytes(p.Bytes);
            UpdateMode();
        }

        void OnDownloadFinished(string path)
        {
            downloading = false;
            AddToShelf(path);
            UpdateMode();
            if (!popDownloads) return;
            var icon = FileIcon(path);
            ShowPeek(null, icon, "", Ui.C("#0A84FF"), "Downloaded", System.IO.Path.GetFileName(path) + " · on the shelf", false);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_CLIPBOARDUPDATE)
            {
                clipTimer.Stop();
                clipTimer.Start();
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                SetHidden(!hidden);
                handled = true;
            }
            else if (msg == Native.WM_MOUSEHWHEEL && view == ViewExpanded && !launcherOpen)
            {
                // Sideways scroll (trackpad swipe or tilt wheel) flips between apps.
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (Math.Abs(delta) >= 60) Flip(delta > 0 ? 1 : -1);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------- Views and motion ----------

        void UpdateMode()
        {
            int m = prompting ? ModePrompter
                : focusPhase != PhaseIdle ? ModeFocus
                : downloading ? ModeDownload
                : media.Present ? ModeMusic
                : awake ? ModeAwake : ModeIdle;
            if (m == mode) return;
            mode = m;
            Ui.Reveal(awakeLayer, m == ModeAwake);
            Ui.Reveal(musicLayer, m == ModeMusic);
            Ui.Reveal(focusLayer, m == ModeFocus);
            Ui.Reveal(downloadLayer, m == ModeDownload);
            Ui.Reveal(prompterLayer, m == ModePrompter);
            if (view == ViewCompact) SetView(ViewCompact, true);
        }

        double CompactWidth()
        {
            double w = mode == ModePrompter ? PrompterW : mode == ModeFocus ? FocusW : mode == ModeDownload ? DownloadW
                : mode == ModeMusic ? MusicW : mode == ModeAwake ? AwakeW : IdleW;
            return camOn ? w + 2 * PrivacyPad : w;
        }

        void SetView(int v) { SetView(v, false); }

        void SetView(int v, bool force)
        {
            if (v == view && !force) return;
            if (v != ViewPeek) peekTimer.Interval = TimeSpan.FromSeconds(3.5);
            if (v != ViewExpanded) EndTyping();
            view = v;
            Ui.Reveal(compact, v == ViewCompact);
            Ui.Reveal(peek, v == ViewPeek);
            Ui.Reveal(hud, v == ViewHud);
            Ui.Reveal(expanded, v == ViewExpanded);
            if (v == ViewExpanded)
            {
                startupChip.Selected = GetStartup();
                UpdateClock();
                // Open on whatever is live right now.
                int open = prompting ? TabPrompter : focusPhase != PhaseIdle ? TabFocus : media.Present && media.Playing ? TabMusic : tab;
                SetTab(open);
                width.Target = EW;
                height.Target = ExpandedHeight(tab);
            }
            else if (v == ViewPeek)
            {
                width.Target = PeekW;
                height.Target = PeekH;
            }
            else if (v == ViewHud)
            {
                width.Target = HudW;
                height.Target = CH;
            }
            else
            {
                width.Target = CompactWidth();
                height.Target = CH;
            }
            UpdateServices();
            UpdateWash();
            StartAnimation();
        }

        // Album-colour wash behind the player, like the island picking up the cover's light.
        void UpdateWash()
        {
            bool on = media.Present && ((view == ViewExpanded && tab == TabMusic && !launcherOpen) || (view == ViewPeek && peekEq.Root.Visibility == Visibility.Visible));
            Ui.Animate(wash, OpacityProperty, on ? 1 : 0, 400);
        }

        double ExpandedHeight(int index)
        {
            var page = launcherOpen ? launcher : pages[index];
            page.Measure(new Size(ContentW, double.PositiveInfinity));
            return ExpandedTop + page.DesiredSize.Height + ExpandedBottom;
        }

        // Re-fit the island when the open page's content changes size.
        void RefitPage(int index)
        {
            if (view != ViewExpanded || tab != index) return;
            height.Target = ExpandedHeight(index);
            StartAnimation();
        }

        void StartAnimation()
        {
            if (animating) return;
            animating = true;
            lastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(now - lastFrame, 1 / 30.0);
            lastFrame = now;
            bool moving = false;
            for (int i = 0; i < 4; i++)
            {
                moving |= width.Step(dt / 4);
                moving |= height.Step(dt / 4);
            }
            ApplyGeometry();
            if (!moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                animating = false;
            }
        }

        void ApplyGeometry()
        {
            double w = Math.Max(width.Value, 40), h = Math.Max(height.Value, 20);
            double p = Clamp((h - CH) / (160 - CH));
            double ear = notch ? 8 + 6 * p : 0;
            double r = notch ? 12 + 18 * p : CH / 2 + (32 - CH / 2) * p;
            r = Math.Min(r, h / 2);
            if (notch && h - r < ear) r = Math.Max(0, h - ear);

            var geometry = IslandShape(w, h, r, ear);
            island.Width = w + 2 * ear;
            island.Height = h;
            shape.Data = geometry;
            clipHost.Clip = geometry;
            inner.Margin = new Thickness(ear, 0, ear, 0);
            expandedScale.ScaleX = expandedScale.ScaleY = 0.94 + 0.06 * Clamp((h - CH) / 140);
        }

        // Notch: flat top glued to the screen edge, concave "shoulders" flowing into it, rounded bottom.
        // Pill: a free-floating rounded rectangle.
        Geometry IslandShape(double w, double h, double r, double ear)
        {
            if (!notch) return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            var f = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
            f.Segments.Add(new LineSegment(new Point(w + 2 * ear, 0), true));
            f.Segments.Add(new ArcSegment(new Point(w + ear, ear), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true));
            f.Segments.Add(new LineSegment(new Point(w + ear, h - r), true));
            f.Segments.Add(new ArcSegment(new Point(w + ear - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            f.Segments.Add(new LineSegment(new Point(ear + r, h), true));
            f.Segments.Add(new ArcSegment(new Point(ear, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            f.Segments.Add(new LineSegment(new Point(ear, ear), true));
            f.Segments.Add(new ArcSegment(new Point(0, 0), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(f);
            return g;
        }

        static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        // ---------- Style, placement, gestures ----------

        void ApplyStyle()
        {
            var c = Ui.C(ThemeColors[theme]);
            shape.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Opacities[opacityIndex]), c.R, c.G, c.B));
            island.Margin = new Thickness(0, notch ? 0 : 8, 0, 0);
            PlaceWindow();
            ApplyGeometry();
        }

        // Click the pill to open it; click an empty spot on the open island to pin it open (again to let go).
        void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!notch)
            {
                // Only the floating pill can be dragged; the notch stays glued to the top.
                double l = Left, t = Top;
                try { DragMove(); } catch (InvalidOperationException) { }
                if (Math.Abs(Left - l) + Math.Abs(Top - t) > 2)
                {
                    ClampToScreen();
                    savedLeft = Left;
                    savedTop = Top;
                    SaveSettings();
                    return;
                }
            }
            if (view != ViewExpanded)
            {
                hoverTimer.Stop();
                SetView(ViewExpanded);
            }
            else
            {
                pinned = !pinned;
                UpdatePin();
                ShowToast("", pinned ? "Pinned open" : "Unpinned");
            }
        }

        double DeviceScale()
        {
            var source = PresentationSource.FromVisual(this);
            return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
        }

        void PlaceWindow()
        {
            // Screenshot mode renders off-screen.
            if (Program.Demo) { Left = -20000; Top = 0; return; }
            if (!notch && !double.IsNaN(savedLeft) && !double.IsNaN(savedTop))
            {
                Left = savedLeft;
                Top = savedTop;
                ClampToScreen();
                return;
            }
            var screen = followMouse ? WinForms.Screen.FromPoint(WinForms.Cursor.Position) : WinForms.Screen.PrimaryScreen;
            lastScreen = screen;
            double scale = DeviceScale();
            Left = (screen.Bounds.Left + screen.Bounds.Width / 2.0) / scale - Width / 2;
            Top = screen.Bounds.Top / scale;
        }

        void FollowMouse()
        {
            if (view == ViewExpanded) return;
            var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            if (lastScreen == null || screen.DeviceName != lastScreen.DeviceName) PlaceWindow();
        }

        void ClampToScreen()
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            Left = Math.Max(vl, Math.Min(Left, vl + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Max(vt, Math.Min(Top, vt + SystemParameters.VirtualScreenHeight - Height));
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(PlaceWindow));
        }

        void SetHidden(bool value)
        {
            hidden = value;
            if (hidden) Hide();
            else
            {
                Show();
                Topmost = false;
                Topmost = true;
            }
            UpdateTray();
        }

        // ---------- Menu (tray icon and right-click on the island) ----------

        readonly List<Action> menuRefreshers = new List<Action>();
        WinForms.ToolStripMenuItem awakeItem, hideItem;

        void BuildMenu()
        {
            menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            awakeItem = Item(menu.Items, "Pause Always On", delegate { SetAwake(!awake, durationIndex, true); });
            Toggle(menu.Items, "Pin island open", () => pinned, v => { pinned = v; UpdatePin(); if (v) SetView(ViewExpanded); });
            Toggle(menu.Items, "Expand on hover", () => hoverExpand, v => hoverExpand = v);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var themeMenu = Item(menu.Items, "Theme", null);
            for (int i = 0; i < ThemeNames.Length; i++)
            {
                int index = i;
                Radio(themeMenu.DropDownItems, ThemeNames[i], () => theme == index, delegate { theme = index; ApplyStyle(); });
            }
            var opacityMenu = Item(menu.Items, "Transparency", null);
            for (int i = 0; i < Opacities.Length; i++)
            {
                int index = i;
                Radio(opacityMenu.DropDownItems, i == 0 ? "Solid" : Math.Round(Opacities[i] * 100) + "% opaque", () => opacityIndex == index,
                    delegate { opacityIndex = index; ApplyStyle(); });
            }
            var shapeMenu = Item(menu.Items, "Shape", null);
            Radio(shapeMenu.DropDownItems, "Notch (attached to top)", () => notch, delegate { notch = true; ApplyStyle(); });
            Radio(shapeMenu.DropDownItems, "Floating pill", () => !notch, delegate { notch = false; ApplyStyle(); });
            var screenMenu = Item(menu.Items, "Show on", null);
            Radio(screenMenu.DropDownItems, "Main screen", () => !followMouse, delegate { followMouse = false; PlaceWindow(); });
            Radio(screenMenu.DropDownItems, "Screen with the mouse", () => followMouse, delegate { followMouse = true; PlaceWindow(); });
            var popMenu = Item(menu.Items, "Pop-ups", null);
            Toggle(popMenu.DropDownItems, "Song changes", () => popSongs, v => popSongs = v);
            Toggle(popMenu.DropDownItems, "Volume and brightness", () => popHud, v => popHud = v);
            Toggle(popMenu.DropDownItems, "Copied text and files", () => popClipboard, v => popClipboard = v);
            Toggle(popMenu.DropDownItems, "Notifications", () => popNotifications, v => popNotifications = v);
            Toggle(popMenu.DropDownItems, "Headphones connecting", () => popBluetooth, v => popBluetooth = v);
            Toggle(popMenu.DropDownItems, "Downloads", () => popDownloads, v => { popDownloads = v; if (!v) { downloading = false; UpdateMode(); } });
            menu.Items.Add(new WinForms.ToolStripSeparator());

            hideItem = Item(menu.Items, "Hide island", delegate { SetHidden(!hidden); });
            hideItem.ShortcutKeyDisplayString = "Ctrl+Alt+D";
            Item(menu.Items, "Reset position", delegate
            {
                savedLeft = savedTop = double.NaN;
                PlaceWindow();
                SaveSettings();
                SetHidden(false);
            });
            Item(menu.Items, "Quit " + Program.Name, delegate { Quit(); });
            menu.Opening += delegate { foreach (var r in menuRefreshers) r(); };
            menu.Closed += delegate { SaveSettings(); };

            tray = new WinForms.NotifyIcon { ContextMenuStrip = menu, Icon = iconOn, Text = Program.Name, Visible = !Program.Demo };
            tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) SetHidden(!hidden); };
        }

        WinForms.ToolStripMenuItem Item(WinForms.ToolStripItemCollection items, string text, EventHandler onClick)
        {
            var item = new WinForms.ToolStripMenuItem(text, null, onClick);
            items.Add(item);
            return item;
        }

        void Toggle(WinForms.ToolStripItemCollection items, string text, Func<bool> get, Action<bool> set)
        {
            var item = Item(items, text, null);
            item.Click += delegate { set(!get()); SaveSettings(); };
            menuRefreshers.Add(() => item.Checked = get());
        }

        void Radio(WinForms.ToolStripItemCollection items, string text, Func<bool> get, EventHandler onClick)
        {
            var item = Item(items, text, onClick);
            item.Click += delegate { SaveSettings(); };
            menuRefreshers.Add(() => item.Checked = get());
        }

        void ShowMenu()
        {
            menu.Show(WinForms.Cursor.Position);
            // The island never takes focus, so give it to the menu or it won't close on outside clicks.
            Native.SetForegroundWindow(menu.Handle);
        }

        void UpdateTray()
        {
            if (tray == null) return;
            awakeItem.Text = awake ? "Pause Always On" : "Keep PC awake";
            hideItem.Text = hidden ? "Show island" : "Hide island";
            tray.Icon = awake ? iconOn : iconOff;
            tray.Text = Program.Name + (awake ? " — keeping PC awake" : "");
        }

        static Drawing.Icon MakeTrayIcon(bool on)
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var bg = RoundRect(0, 0, 32, 32, 8))
                    using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(22, 22, 24)))
                        g.FillPath(brush, bg);
                    using (var pill = RoundRect(4, 11, 24, 10, 5))
                    using (var brush = new Drawing.SolidBrush(on ? Drawing.Color.FromArgb(48, 209, 88) : Drawing.Color.FromArgb(142, 142, 147)))
                        g.FillPath(brush, pill);
                }
                IntPtr handle = bmp.GetHicon();
                var icon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
                Native.DestroyIcon(handle);
                return icon;
            }
        }

        static Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var path = new Drawing.Drawing2D.GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---------- Settings and startup ----------

        // Lumi used to be called Agents Always On: carry over its settings and start-with-Windows entry.
        // Also keeps start-with-Windows pointing at this copy if Lumi was moved or reinstalled.
        static void MigrateFromOldName()
        {
            try
            {
                if (!File.Exists(SettingsFile) && File.Exists(OldSettingsFile))
                {
                    Directory.CreateDirectory(Store.Folder);
                    File.Copy(OldSettingsFile, SettingsFile);
                }
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null && key.GetValue(OldRunName) != null)
                    {
                        key.DeleteValue(OldRunName, false);
                        key.SetValue(RunName, "\"" + ExePath + "\"");
                    }
                    var current = key == null ? null : key.GetValue(RunName) as string;
                    if (current != null && !string.Equals(current.Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase))
                        key.SetValue(RunName, "\"" + ExePath + "\"");
                }
            }
            catch { }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    double d;
                    int n;
                    bool on = value == "1";
                    switch (key)
                    {
                        case "screenOn": screenOn = on; break;
                        case "style": notch = value != "pill"; break;
                        case "pinned": pinned = on; break;
                        case "hover": hoverExpand = on; break;
                        case "follow": followMouse = on; break;
                        case "popSongs": popSongs = on; break;
                        case "popHud": popHud = on; break;
                        case "popClipboard": popClipboard = on; break;
                        case "popNotifications": popNotifications = on; break;
                        case "popBluetooth": popBluetooth = on; break;
                        case "popDownloads": popDownloads = on; break;
                        case "theme": if (int.TryParse(value, out n) && n >= 0 && n < ThemeNames.Length) theme = n; break;
                        case "opacity": if (int.TryParse(value, out n) && n >= 0 && n < Opacities.Length) opacityIndex = n; break;
                        case "focusMinutes": if (int.TryParse(value, out n) && n >= 5 && n <= 120) focusMinutes = n; break;
                        case "wpm": if (int.TryParse(value, out n) && n >= 60 && n <= 260) promptWpm = n; break;
                        case "left": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) savedLeft = d; break;
                        case "top": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) savedTop = d; break;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (Program.Demo) return;
            try
            {
                Func<bool, string> b = v => v ? "1" : "0";
                Directory.CreateDirectory(Store.Folder);
                File.WriteAllLines(SettingsFile, new[]
                {
                    "screenOn=" + b(screenOn),
                    "style=" + (notch ? "notch" : "pill"),
                    "pinned=" + b(pinned),
                    "hover=" + b(hoverExpand),
                    "follow=" + b(followMouse),
                    "popSongs=" + b(popSongs),
                    "popHud=" + b(popHud),
                    "popClipboard=" + b(popClipboard),
                    "popNotifications=" + b(popNotifications),
                    "popBluetooth=" + b(popBluetooth),
                    "popDownloads=" + b(popDownloads),
                    "theme=" + theme,
                    "opacity=" + opacityIndex,
                    "focusMinutes=" + focusMinutes,
                    "wpm=" + promptWpm,
                    "left=" + (double.IsNaN(savedLeft) ? "" : savedLeft.ToString(CultureInfo.InvariantCulture)),
                    "top=" + (double.IsNaN(savedTop) ? "" : savedTop.ToString(CultureInfo.InvariantCulture))
                });
            }
            catch { }
        }

        static string ExePath { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }

        static bool GetStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue(RunName, "\"" + ExePath + "\"");
                    else key.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        // ---------- Shutdown ----------

        void Quit()
        {
            Cleanup();
            Application.Current.Shutdown();
        }

        public void Cleanup()
        {
            if (cleanedUp) return;
            cleanedUp = true;
            SaveSettings();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            brightness.Stop();
            if (hwnd != IntPtr.Zero)
            {
                Native.UnregisterHotKey(hwnd, HotkeyId);
                Native.RemoveClipboardFormatListener(hwnd);
            }
            tray.Visible = false;
            tray.Dispose();
        }
    }
}
