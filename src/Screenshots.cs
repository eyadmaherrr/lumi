// Screenshot mode (Lumi.exe --screenshots <folder>): fills the island with made-up sample data,
// steps through its views and renders each one to a PNG for the README. Runs off-screen and never
// reads or writes your real notes, todos, settings or clipboard.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lumi
{
    partial class IslandWindow
    {
        public void RunScreenshots(string folder)
        {
            Directory.CreateDirectory(folder);
            var shots = new List<KeyValuePair<string, Action>>();
            Action<string, Action> shot = (name, setup) => shots.Add(new KeyValuePair<string, Action>(name, setup));
            var art = SampleArt();

            shot(null, LoadSampleData);
            shot("compact-music", () => { OnMedia(SampleMedia(art)); SetView(ViewCompact, true); });
            shot("compact-awake", () => { OnMedia(new MediaInfo()); SetView(ViewCompact, true); });
            shot("peek-song", () => { OnMedia(SampleMedia(art)); ShowPeek(ToImage(art), null, null, MusicGreen, "Midnight City", "M83", true); });
            shot("peek-airpods", () => OnDevice(new DeviceEvent { Name = "AirPods Pro", Connected = true, Battery = 82 }));
            shot("hud-volume", () => ShowHud("", 0.62, null));
            shot("music", () => Open(TabMusic));
            shot("ai", () => Open(TabAi));
            shot("weather", () => Open(TabWeather));
            shot("system", () => Open(TabSystem));
            shot("todos", () => Open(TabTodos));
            shot("clipboard", () => Open(TabClipboard));
            shot("colour", () => Open(TabColor));
            shot("devices", () => Open(TabDevices));
            shot("dev", () => Open(TabDev));
            shot("launcher", () => { Open(TabMusic); ToggleLauncher(); });
            shot("focus", () =>
            {
                if (launcherOpen) ToggleLauncher();
                focusPhase = PhaseFocus;
                focusDone = 2;
                focusEnd = DateTime.Now + TimeSpan.FromMinutes(17.7);
                UpdateFocus();
                UpdateMode();
                Open(TabFocus);
            });
            shot("compact-focus", () => SetView(ViewCompact, true));

            int index = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += delegate
            {
                // Each shot is captured one tick after its setup, once every spring and fade has settled.
                if (index > 0 && shots[index - 1].Key != null) Capture(Path.Combine(folder, shots[index - 1].Key + ".png"));
                if (index >= shots.Count)
                {
                    timer.Stop();
                    Application.Current.Shutdown();
                    return;
                }
                shots[index++].Value();
            };
            timer.Start();
        }

        void Open(int page)
        {
            SetView(ViewExpanded, true);
            SetTab(page);
        }

        static MediaInfo SampleMedia(byte[] art)
        {
            return new MediaInfo
            {
                Present = true,
                Playing = true,
                CanSeek = true,
                Title = "Midnight City",
                Artist = "M83",
                Source = "Spotify",
                Key = "demo",
                Art = art,
                Position = TimeSpan.FromSeconds(93),
                Duration = TimeSpan.FromSeconds(243),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        void LoadSampleData()
        {
            var now = DateTime.Now;
            todos.Add(new Todo { Text = "Review the pull request", Due = now.Date.AddHours(Math.Min(23, now.Hour + 2)) });
            todos.Add(new Todo { Text = "Pay rent", Due = now.Date.AddDays(1).AddHours(17) });
            todos.Add(new Todo { Text = "Book flights", Due = now.Date.AddDays(4).AddHours(9) });
            todos.Add(new Todo { Text = "Water the plants", Done = true });
            RebuildTodos();
            RebuildCalendar();

            notes.Add(new Note { Text = "Standup at 10: demo the island", Pinned = true });
            notes.Add(new Note { Text = "Ideas: clipboard search, focus stats" });
            RebuildNotes();

            clips.Add(new ClipEntry { Kind = "Colours", Text = "#FF6B4A", Swatch = Color.FromRgb(0xFF, 0x6B, 0x4A), Time = now.AddMinutes(-1) });
            clips.Add(new ClipEntry { Kind = "Links", Text = "https://github.com/eyadmaherrr/lumi?utm_source=newsletter", Time = now.AddMinutes(-4) });
            clips.Add(new ClipEntry { Kind = "Text", Text = "npm run dev -- --turbo", Time = now.AddMinutes(-9) });
            clips.Add(new ClipEntry { Kind = "Images", Text = "1920 × 1080", Image = Decode(SampleArt()), Time = now.AddMinutes(-26) });
            clips.Add(new ClipEntry { Kind = "Text", Text = "Thanks, see you tomorrow!", Time = now.AddHours(-2) });

            foreach (var hex in new[] { "#FF6B4A", "#30D158", "#0A84FF", "#BF5AF2", "#FFD60A", "#64D2FF" })
            {
                Color c;
                if (TryParseColor(hex, out c)) recentColors.Add(c);
            }
            SetPick(Color.FromRgb(0x5E, 0x5C, 0xE6));

            OnAiTools(new List<AiTool>
            {
                new AiTool { Name = "Claude", Cpu = 18, Working = true },
                new AiTool { Name = "Codex", Cpu = 7, Working = true },
                new AiTool { Name = "ChatGPT", Cpu = 0 }
            });
            OnDevices(new List<DeviceRow>
            {
                new DeviceRow { Name = "AirPods Pro", Glyph = "", Connected = true, Battery = 82 },
                new DeviceRow { Name = "Magic Keyboard", Glyph = "", Connected = true, Battery = 64 },
                new DeviceRow { Name = "iPhone", Glyph = "", Connected = false }
            });
            OnDevServers(new List<DevServer>
            {
                new DevServer { Port = 3000, Pid = 18244, Process = "node", Project = "my-app", Tool = "Next.js" },
                new DevServer { Port = 5173, Pid = 20116, Process = "node", Project = "docs", Tool = "Vite" }
            });

            var weatherSample = new WeatherInfo
            {
                Ok = true, Metric = true, Place = "Lisbon", Description = "Partly cloudy", Code = 116,
                Temp = 24, FeelsLike = 25, Humidity = 48, Wind = 14, High = 27, Low = 18
            };
            var hour = DateTime.Today.AddHours(now.Hour);
            int[] codes = { 116, 113, 113, 116, 119, 176 };
            double[] temps = { 24, 25, 26, 24, 21, 19 };
            for (int i = 0; i < 6; i++) weatherSample.Hours.Add(new WeatherHour { Time = hour.AddHours(i * 3), Code = codes[i], Temp = temps[i] });
            weatherSample.Days.Add(new WeatherDay { Date = DateTime.Today, Code = 116, High = 27, Low = 18 });
            weatherSample.Days.Add(new WeatherDay { Date = DateTime.Today.AddDays(1), Code = 113, High = 29, Low = 19 });
            weatherSample.Days.Add(new WeatherDay { Date = DateTime.Today.AddDays(2), Code = 176, High = 22, Low = 16 });
            OnWeather(weatherSample);

            var random = new Random(4);
            double cpu = 22;
            for (int i = 0; i < 60; i++)
            {
                cpu = Math.Max(4, Math.Min(78, cpu + random.Next(-9, 10)));
                OnStats(new SystemSnapshot
                {
                    Cpu = cpu, Gpu = 14, HasGpu = true, Ram = 58, RamUsedGb = 18.6, RamTotalGb = 32, Disk = 64, DiskFreeGb = 342, Drive = "C:",
                    DownBps = 2.4e6, UpBps = 3.1e5, DiskReadBps = 1.2e6, DiskWriteBps = 4.5e5
                });
            }
            musicVolume.Set(0.62, false);
            musicVolumeText.Text = "62";
        }

        // A made-up album cover: dusk sky, sun and a city skyline.
        static byte[] SampleArt()
        {
            const int size = 300;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x0B, 0x3D), 0));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x8E, 0x2D, 0xE2), 0.55));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x5F, 0x6D), 1));
                dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC3, 0x71)), null, new Point(size * 0.62, size * 0.58), 56, 56);
                var city = new SolidColorBrush(Color.FromRgb(0x14, 0x08, 0x2E));
                var random = new Random(7);
                for (double x = 0; x < size; x += 26)
                {
                    double h = 50 + random.Next(0, 110);
                    dc.DrawRectangle(city, null, new Rect(x, size - h, 24, h));
                }
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        static BitmapSource Decode(byte[] png) { return ToImage(png); }

        // The island at 2x, on a dusky "desktop" so the notch reads as sitting at the top of a screen.
        void Capture(string file)
        {
            const double scale = 2, pad = 44, minWidth = 460, minHeight = 120;
            var root = (FrameworkElement)Content;
            root.UpdateLayout();
            var position = island.TranslatePoint(new Point(0, 0), root);
            double width = Math.Min(root.ActualWidth, Math.Max(minWidth, island.ActualWidth + pad * 2));
            double height = Math.Min(root.ActualHeight, Math.Max(minHeight, island.ActualHeight + pad * 1.4));
            double left = Math.Max(0, Math.Min(root.ActualWidth - width, position.X + island.ActualWidth / 2 - width / 2));

            var full = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            full.Render(root);
            var crop = new CroppedBitmap(full, new Int32Rect((int)(left * scale), 0, (int)(width * scale), (int)(height * scale)));

            var desktop = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            desktop.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x1A, 0x52), 0));
            desktop.GradientStops.Add(new GradientStop(Color.FromRgb(0x4C, 0x2A, 0x85), 0.55));
            desktop.GradientStops.Add(new GradientStop(Color.FromRgb(0xB5, 0x57, 0x7C), 1));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(desktop, null, new Rect(0, 0, width, height));
                dc.DrawImage(crop, new Rect(0, 0, width, height));
            }
            var output = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            output.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
    }
}
