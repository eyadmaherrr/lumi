// Background data sources: now playing (any app), Windows notifications, system stats, weather,
// camera use, Bluetooth devices and screen brightness. Each raises events on its own thread;
// the window marshals them onto the UI thread.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using DevEnum = Windows.Devices.Enumeration;
using WinRT = Windows.Foundation;

namespace Lumi
{
    // Async WinRT calls are waited on by polling their status, since the .NET 4 compiler can't await them.
    static class WinRt
    {
        public static T Wait<T>(WinRT.IAsyncOperation<T> op)
        {
            while (op.Status == WinRT.AsyncStatus.Started) Thread.Sleep(5);
            return op.GetResults();
        }

        public static byte[] ReadAll(IRandomAccessStreamReference reference)
        {
            if (reference == null) return null;
            try
            {
                using (var stream = Wait(reference.OpenReadAsync()))
                using (var reader = new DataReader(stream))
                {
                    uint loaded = Wait(reader.LoadAsync((uint)stream.Size));
                    var bytes = new byte[loaded];
                    reader.ReadBytes(bytes);
                    return bytes.Length > 0 ? bytes : null;
                }
            }
            catch { return null; }
        }
    }

    // ---------- Now playing, from any app that reports to Windows' media controls ----------

    class MediaInfo
    {
        public bool Present, Playing, CanSeek;
        public string Title, Artist, Key, Source;
        public byte[] Art;
        public TimeSpan Position, Duration;
        public DateTime UpdatedUtc;
    }

    class MediaWatcher
    {
        public const int TogglePlay = 1, Next = 2, Previous = 3, Seek = 4;
        public event Action<MediaInfo> Changed;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        int command;
        long seekTicks;

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "Media" }.Start(); }

        public void Send(int cmd)
        {
            Interlocked.Exchange(ref command, cmd);
            wake.Set();
        }

        public void SeekTo(TimeSpan position)
        {
            Interlocked.Exchange(ref seekTicks, position.Ticks);
            Send(Seek);
        }

        void Loop()
        {
            GlobalSystemMediaTransportControlsSessionManager manager = null;
            string lastKey = null, lastSession = null;
            byte[] art = null;
            while (true)
            {
                var info = new MediaInfo();
                try
                {
                    if (manager == null) manager = WinRt.Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    var session = Pick(manager, lastSession);
                    lastSession = session != null ? session.SourceAppUserModelId : null;

                    int cmd = Interlocked.Exchange(ref command, 0);
                    if (session != null && cmd != 0)
                    {
                        if (cmd == TogglePlay) WinRt.Wait(session.TryTogglePlayPauseAsync());
                        else if (cmd == Next) WinRt.Wait(session.TrySkipNextAsync());
                        else if (cmd == Previous) WinRt.Wait(session.TrySkipPreviousAsync());
                        else if (cmd == Seek) WinRt.Wait(session.TryChangePlaybackPositionAsync(Interlocked.Read(ref seekTicks)));
                        Thread.Sleep(250);
                    }

                    if (session != null)
                    {
                        var props = WinRt.Wait(session.TryGetMediaPropertiesAsync());
                        if (props != null && !string.IsNullOrEmpty(props.Title))
                        {
                            string key = session.SourceAppUserModelId + "\n" + props.Title + "\n" + props.Artist;
                            // Covers sometimes arrive a moment after the title, so retry until we have one.
                            if (key != lastKey || art == null)
                            {
                                if (key != lastKey) art = null;
                                lastKey = key;
                                art = WinRt.ReadAll(props.Thumbnail);
                            }
                            var playback = session.GetPlaybackInfo();
                            var timeline = session.GetTimelineProperties();
                            info.Present = true;
                            info.Title = props.Title;
                            info.Artist = props.Artist;
                            info.Key = key;
                            info.Art = art;
                            info.Source = Friendly(session.SourceAppUserModelId);
                            info.Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            info.CanSeek = playback.Controls.IsPlaybackPositionEnabled;
                            info.Position = timeline.Position;
                            info.Duration = timeline.EndTime - timeline.StartTime;
                            info.UpdatedUtc = timeline.LastUpdatedTime.UtcDateTime;
                        }
                    }
                }
                catch { manager = null; }
                var handler = Changed;
                if (handler != null) handler(info);
                wake.WaitOne(1000);
            }
        }

        // Whatever is playing wins; otherwise stay with the last app, then Windows' own pick.
        static GlobalSystemMediaTransportControlsSession Pick(GlobalSystemMediaTransportControlsSessionManager manager, string last)
        {
            GlobalSystemMediaTransportControlsSession sticky = null;
            foreach (var s in manager.GetSessions())
            {
                if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
                if (s.SourceAppUserModelId == last) sticky = s;
            }
            return sticky ?? manager.GetCurrentSession();
        }

        public static string Friendly(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Media";
            var lower = id.ToLowerInvariant();
            string[,] known =
            {
                { "spotify", "Spotify" }, { "msedge", "Edge" }, { "chrome", "Chrome" }, { "firefox", "Firefox" }, { "brave", "Brave" },
                { "opera", "Opera" }, { "vlc", "VLC" }, { "zunemusic", "Media Player" }, { "applemusic", "Apple Music" },
                { "itunes", "iTunes" }, { "foobar", "foobar2000" }, { "tidal", "TIDAL" }, { "deezer", "Deezer" }, { "youtube", "YouTube" }
            };
            for (int i = 0; i < known.GetLength(0); i++)
                if (lower.Contains(known[i, 0])) return known[i, 1];
            var name = id.Split('!')[0];
            name = name.Substring(name.LastIndexOfAny(new[] { '\\', '.' }) + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name.Length > 0 ? char.ToUpper(name[0]) + name.Substring(1) : "Media";
        }
    }

    // ---------- Windows notifications ----------

    class Notice
    {
        public string App, Title, Body;
        public byte[] Logo;
    }

    // Polls the notification centre (needs Settings > Privacy > Notifications > "Let apps access
    // your notifications"). Only notifications that arrive after start-up are announced.
    class NotificationWatcher
    {
        public event Action<Notice> Arrived;
        public bool Allowed { get; private set; }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "Notifications" }.Start(); }

        void Loop()
        {
            UserNotificationListener listener;
            try
            {
                listener = UserNotificationListener.Current;
                var status = listener.GetAccessStatus();
                if (status == UserNotificationListenerAccessStatus.Unspecified) status = WinRt.Wait(listener.RequestAccessAsync());
                Allowed = status == UserNotificationListenerAccessStatus.Allowed;
            }
            catch { return; }
            if (!Allowed) return;

            var seen = new HashSet<uint>();
            bool first = true;
            while (true)
            {
                try
                {
                    var list = WinRt.Wait(listener.GetNotificationsAsync(NotificationKinds.Toast));
                    foreach (var n in list)
                    {
                        if (!seen.Add(n.Id) || first) continue;
                        var notice = Read(n);
                        var handler = Arrived;
                        if (notice != null && handler != null) handler(notice);
                    }
                    first = false;
                }
                catch { }
                Thread.Sleep(1500);
            }
        }

        static Notice Read(UserNotification n)
        {
            try
            {
                var notice = new Notice();
                try { notice.App = n.AppInfo.DisplayInfo.DisplayName; } catch { notice.App = "Notification"; }
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding != null)
                {
                    var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (texts.Count > 0) notice.Title = texts[0];
                    if (texts.Count > 1) notice.Body = string.Join(" ", texts.Skip(1));
                }
                try { notice.Logo = WinRt.ReadAll(n.AppInfo.DisplayInfo.GetLogo(new WinRT.Size(64, 64))); } catch { }
                return notice;
            }
            catch { return null; }
        }
    }

    // ---------- System monitor ----------

    class SystemSnapshot
    {
        public double Cpu, Gpu, Ram, Disk;
        public double RamUsedGb, RamTotalGb, DiskFreeGb;
        public double DownBps, UpBps, DiskReadBps, DiskWriteBps;
        public bool HasGpu;
        public string Drive;
    }

    // Samples once a second, only while the System page is open.
    class SystemStats
    {
        public event Action<SystemSnapshot> Changed;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool active;

        public bool Active
        {
            get { return active; }
            set { active = value; if (value) wake.Set(); }
        }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "System stats", Priority = ThreadPriority.BelowNormal }.Start(); }

        void Loop()
        {
            PerformanceCounter cpu = null, diskRead = null, diskWrite = null;
            var gpu = new List<PerformanceCounter>();
            int gpuAge = int.MaxValue;
            long lastRx = 0, lastTx = 0;
            DateTime lastNet = DateTime.MinValue;

            while (true)
            {
                if (!active) { wake.WaitOne(); continue; }
                var snap = new SystemSnapshot();
                try
                {
                    if (cpu == null) cpu = Counter("Processor Information", "% Processor Utility", "_Total") ?? Counter("Processor", "% Processor Time", "_Total");
                    if (cpu != null) snap.Cpu = Math.Min(100, cpu.NextValue());
                    if (diskRead == null) diskRead = Counter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    if (diskWrite == null) diskWrite = Counter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    if (diskRead != null) snap.DiskReadBps = diskRead.NextValue();
                    if (diskWrite != null) snap.DiskWriteBps = diskWrite.NextValue();

                    // GPU: sum the 3D engine load of every process, like Task Manager. The instance list
                    // changes as apps start and stop, so it's rebuilt every few seconds.
                    if (gpuAge++ > 8)
                    {
                        gpuAge = 0;
                        foreach (var c in gpu) c.Dispose();
                        gpu.Clear();
                        try
                        {
                            foreach (var name in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                                if (name.EndsWith("engtype_3D", StringComparison.Ordinal))
                                {
                                    var c = Counter("GPU Engine", "Utilization Percentage", name);
                                    if (c != null) gpu.Add(c);
                                }
                        }
                        catch { }
                    }
                    double g = 0;
                    foreach (var c in gpu) { try { g += c.NextValue(); } catch { } }
                    snap.HasGpu = gpu.Count > 0;
                    snap.Gpu = Math.Min(100, g);

                    var mem = new Native.MemoryStatus();
                    if (Native.GlobalMemoryStatusEx(mem))
                    {
                        snap.RamTotalGb = mem.TotalPhys / 1073741824.0;
                        snap.RamUsedGb = (mem.TotalPhys - mem.AvailPhys) / 1073741824.0;
                        snap.Ram = 100.0 * (mem.TotalPhys - mem.AvailPhys) / mem.TotalPhys;
                    }

                    var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                    snap.Drive = drive.Name.TrimEnd('\\');
                    snap.Disk = 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
                    snap.DiskFreeGb = drive.TotalFreeSpace / 1073741824.0;

                    long rx = 0, tx = 0;
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var stats = nic.GetIPStatistics();
                        rx += stats.BytesReceived;
                        tx += stats.BytesSent;
                    }
                    var now = DateTime.UtcNow;
                    double seconds = (now - lastNet).TotalSeconds;
                    if (lastNet != DateTime.MinValue && seconds > 0)
                    {
                        snap.DownBps = Math.Max(0, (rx - lastRx) / seconds);
                        snap.UpBps = Math.Max(0, (tx - lastTx) / seconds);
                    }
                    lastRx = rx; lastTx = tx; lastNet = now;
                }
                catch { }
                var handler = Changed;
                if (handler != null) handler(snap);
                wake.WaitOne(1000);
            }
        }

        static PerformanceCounter Counter(string category, string counter, string instance)
        {
            try
            {
                var c = new PerformanceCounter(category, counter, instance, true);
                c.NextValue();
                return c;
            }
            catch { return null; }
        }
    }

    // ---------- Weather (wttr.in, location from your IP) ----------

    class WeatherHour
    {
        public DateTime Time;
        public int Code;
        public double Temp;
    }

    class WeatherDay
    {
        public DateTime Date;
        public int Code;
        public double High, Low;
    }

    class WeatherInfo
    {
        public bool Ok, Metric;
        public string Place, Description, Error;
        public int Code;
        public double Temp, FeelsLike, Humidity, Wind, High, Low;
        public List<WeatherHour> Hours = new List<WeatherHour>();
        public List<WeatherDay> Days = new List<WeatherDay>();
    }

    class WeatherService
    {
        public event Action<WeatherInfo> Changed;
        DateTime nextFetch = DateTime.MinValue;
        int busy;

        static readonly HashSet<string> ImperialCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "United States of America", "United States", "USA", "Liberia", "Myanmar"
        };

        public void Refresh(bool force)
        {
            if (!force && DateTime.UtcNow < nextFetch) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var info = Fetch();
                // Refresh every 30 minutes; after a failure, try again in 5.
                nextFetch = DateTime.UtcNow.AddMinutes(info.Ok ? 30 : 5);
                Interlocked.Exchange(ref busy, 0);
                var handler = Changed;
                if (handler != null) handler(info);
            });
        }

        static WeatherInfo Fetch()
        {
            var info = new WeatherInfo();
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                var request = (HttpWebRequest)WebRequest.Create("https://wttr.in/?format=j1");
                request.UserAgent = "curl/8.0";
                request.Timeout = 15000;
                string json;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();

                var root = new JavaScriptSerializer().DeserializeObject(json);
                // Units follow where the weather is, not the Windows region setting.
                var country = Convert.ToString(Get(root, "nearest_area", 0, "country", 0, "value"));
                info.Metric = !ImperialCountries.Contains(country);
                string temp = info.Metric ? "tempC" : "tempF";
                var now = Get(root, "current_condition", 0);
                var today = Get(root, "weather", 0);
                info.Temp = Num(Get(now, info.Metric ? "temp_C" : "temp_F"));
                info.FeelsLike = Num(Get(now, info.Metric ? "FeelsLikeC" : "FeelsLikeF"));
                info.Humidity = Num(Get(now, "humidity"));
                info.Wind = Num(Get(now, info.Metric ? "windspeedKmph" : "windspeedMiles"));
                info.Code = (int)Num(Get(now, "weatherCode"));
                info.Description = Convert.ToString(Get(now, "weatherDesc", 0, "value")).Trim();
                info.High = Num(Get(today, info.Metric ? "maxtempC" : "maxtempF"));
                info.Low = Num(Get(today, info.Metric ? "mintempC" : "mintempF"));
                info.Place = Convert.ToString(Get(root, "nearest_area", 0, "areaName", 0, "value"));

                // Forecast: the next few 3-hour slots and each day wttr.in provides.
                var days = Get(root, "weather") as object[] ?? new object[0];
                foreach (var day in days)
                {
                    DateTime date;
                    if (!DateTime.TryParseExact(Convert.ToString(Get(day, "date")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                    var hourly = Get(day, "hourly") as object[] ?? new object[0];
                    info.Days.Add(new WeatherDay
                    {
                        Date = date,
                        High = Num(Get(day, info.Metric ? "maxtempC" : "maxtempF")),
                        Low = Num(Get(day, info.Metric ? "mintempC" : "mintempF")),
                        Code = hourly.Length > 4 ? (int)Num(Get(hourly[4], "weatherCode")) : info.Code
                    });
                    foreach (var h in hourly)
                    {
                        var time = date.AddHours(Num(Get(h, "time")) / 100);
                        if (time > DateTime.Now.AddHours(-1) && info.Hours.Count < 6)
                            info.Hours.Add(new WeatherHour { Time = time, Code = (int)Num(Get(h, "weatherCode")), Temp = Num(Get(h, temp)) });
                    }
                }
                info.Ok = true;
            }
            catch (Exception e) { info.Error = e.Message; }
            return info;
        }

        static object Get(object node, params object[] path)
        {
            foreach (var step in path)
            {
                if (node == null) return null;
                if (step is int)
                {
                    var array = node as object[];
                    node = array != null && array.Length > (int)step ? array[(int)step] : null;
                }
                else
                {
                    var map = node as Dictionary<string, object>;
                    object next;
                    node = map != null && map.TryGetValue((string)step, out next) ? next : null;
                }
            }
            return node;
        }

        static double Num(object value)
        {
            double d;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // wttr.in / WWO condition codes to a symbol.
        public static string Symbol(int code)
        {
            switch (code)
            {
                case 113: return "☀";                         // sunny
                case 116: return "⛅";                         // partly cloudy
                case 119: case 122: return "☁";               // cloudy
                case 143: case 248: case 260: return "♒";     // fog / mist
                case 200: case 386: case 389: case 392: return "⚡"; // thunder
                case 179: case 182: case 185: case 227: case 230: case 317: case 320: case 323: case 326: case 329:
                case 332: case 335: case 338: case 350: case 362: case 365: case 368: case 371: case 374: case 377: case 395:
                    return "❄";                               // snow / sleet / ice
                default: return "☂";                          // rain and drizzle
            }
        }
    }

    // ---------- Camera in use ----------

    // Windows records which apps are using the camera under CapabilityAccessManager:
    // an entry whose LastUsedTimeStop is 0 is in use right now.
    static class Privacy
    {
        const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public static bool InUse(string capability)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Store + capability))
                    return key != null && Scan(key);
            }
            catch { return false; }
        }

        static bool Scan(RegistryKey key)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using (var sub = key.OpenSubKey(name))
                {
                    if (sub == null) continue;
                    if (name == "NonPackaged")
                    {
                        if (Scan(sub)) return true;
                        continue;
                    }
                    var start = sub.GetValue("LastUsedTimeStart");
                    var stop = sub.GetValue("LastUsedTimeStop");
                    if (start is long && (long)start != 0 && stop is long && (long)stop == 0) return true;
                }
            }
            return false;
        }
    }

    // ---------- Bluetooth devices (AirPods, keyboards, phones...) ----------

    class DeviceEvent
    {
        public string Name;
        public bool Connected;
        public int Battery = -1;
    }

    class DeviceRow
    {
        public string Name, Glyph;
        public bool Connected;
        public int Battery = -1;
    }

    // Polls paired Bluetooth devices. Raises Changed when headphones/speakers connect or disconnect
    // (with the battery level Windows shows in Settings), and Devices with the full list while the
    // Devices page is open.
    class BluetoothWatcher
    {
        const string ConnectedKey = "System.Devices.Aep.IsConnected", MajorClassKey = "System.Devices.Aep.Bluetooth.Cod.Major",
            ContainerKey = "System.Devices.Aep.ContainerId", BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        const string Paired = "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
        const string Classic = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
        const string LowEnergy = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
        const int AudioVideo = 4; // Bluetooth major device class for headphones and speakers

        public event Action<DeviceEvent> Changed;
        public event Action<List<DeviceRow>> Devices;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool listActive;

        public bool ListActive
        {
            get { return listActive; }
            set { listActive = value; if (value) wake.Set(); }
        }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "Bluetooth" }.Start(); }

        void Loop()
        {
            var known = new Dictionary<string, bool>();
            var props = new List<string> { ConnectedKey, MajorClassKey, ContainerKey };
            bool first = true;
            while (true)
            {
                try
                {
                    var classic = WinRt.Wait(DevEnum.DeviceInformation.FindAllAsync(Classic + " AND " + Paired, props, DevEnum.DeviceInformationKind.AssociationEndpoint));
                    foreach (var d in classic)
                    {
                        if (Convert.ToInt32(Prop(d, MajorClassKey) ?? 0) != AudioVideo) continue;
                        bool connected = IsConnected(d);
                        bool was, seen = known.TryGetValue(d.Id, out was);
                        known[d.Id] = connected;
                        if (first || (seen ? was == connected : !connected)) continue;

                        var e = new DeviceEvent { Name = d.Name, Connected = connected };
                        if (connected)
                        {
                            // Windows refreshes the battery level a moment after the connection.
                            Thread.Sleep(2500);
                            e.Battery = Battery(Prop(d, ContainerKey));
                        }
                        var handler = Changed;
                        if (handler != null) handler(e);
                    }

                    if (listActive)
                    {
                        var rows = new Dictionary<string, DeviceRow>(StringComparer.OrdinalIgnoreCase);
                        var le = WinRt.Wait(DevEnum.DeviceInformation.FindAllAsync(LowEnergy + " AND " + Paired, props, DevEnum.DeviceInformationKind.AssociationEndpoint));
                        foreach (var d in classic.Concat(le))
                        {
                            // A phone shows up once per radio; keep one row per name, preferring the connected one.
                            DeviceRow row;
                            bool connected = IsConnected(d);
                            if (rows.TryGetValue(d.Name, out row) && (row.Connected || !connected)) continue;
                            rows[d.Name] = new DeviceRow
                            {
                                Name = d.Name,
                                Connected = connected,
                                Glyph = GlyphFor(d.Name, Convert.ToInt32(Prop(d, MajorClassKey) ?? 0)),
                                Battery = Battery(Prop(d, ContainerKey))
                            };
                        }
                        var list = rows.Values.OrderByDescending(r => r.Connected).ThenBy(r => r.Name).ToList();
                        var handler = Devices;
                        if (handler != null) handler(list);
                    }
                }
                catch { }
                first = false;
                wake.WaitOne(listActive ? 3000 : 2000);
            }
        }

        static bool IsConnected(DevEnum.DeviceInformation d)
        {
            var flag = Prop(d, ConnectedKey);
            return flag is bool && (bool)flag;
        }

        static string GlyphFor(string name, int major)
        {
            var n = name.ToLowerInvariant();
            if (major == AudioVideo || n.Contains("airpods") || n.Contains("buds") || n.Contains("head") || n.Contains("ear")) return "";
            if (n.Contains("keyboard")) return "";
            if (n.Contains("mouse") || n.Contains("trackpad")) return "";
            if (n.Contains("phone") || major == 2) return "";
            if (n.Contains("watch")) return "";
            if (n.Contains("controller") || n.Contains("xbox")) return "";
            return "";
        }

        static object Prop(DevEnum.DeviceInformation d, string key)
        {
            object value;
            return d.Properties.TryGetValue(key, out value) ? value : null;
        }

        static int Battery(object container)
        {
            if (!(container is Guid)) return -1;
            try
            {
                var entries = WinRt.Wait(DevEnum.DeviceInformation.FindAllAsync(
                    "System.Devices.ContainerId:=\"{" + container + "}\"", new List<string> { BatteryKey }, DevEnum.DeviceInformationKind.Device));
                foreach (var d in entries)
                {
                    var value = Prop(d, BatteryKey);
                    if (value != null) return Convert.ToInt32(value);
                }
            }
            catch { }
            return -1;
        }
    }

    // ---------- Screen brightness (laptops / monitors that report it) ----------

    class BrightnessWatcher
    {
        public event Action<int> Changed;
        ManagementEventWatcher watcher;

        public void Start()
        {
            try
            {
                watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                watcher.EventArrived += (s, e) =>
                {
                    try
                    {
                        int value = Convert.ToInt32(e.NewEvent["Brightness"]);
                        var handler = Changed;
                        if (handler != null) handler(value);
                    }
                    catch { }
                };
                watcher.Start();
            }
            catch { watcher = null; }
        }

        public void Stop()
        {
            try { if (watcher != null) watcher.Stop(); } catch { }
        }

        public static void Set(int percent)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods")))
                        foreach (ManagementObject m in searcher.Get())
                            m.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Max(0, Math.Min(100, percent)) });
                }
                catch { }
            });
        }
    }
}
