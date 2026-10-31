// Background helpers for the productivity pages: which AI tools are working, local dev servers,
// browser downloads, todo date parsing and on-disk storage for notes and todos.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Lumi
{
    // ---------- Local storage (%APPDATA%\Lumi) ----------

    static class Store
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.Name);

        public static string PathOf(string file) { return Path.Combine(Folder, file); }

        public static string[] Read(string file)
        {
            // Screenshot mode never touches your real notes, todos or colours.
            if (Program.Demo) return new string[0];
            try { return File.Exists(PathOf(file)) ? File.ReadAllLines(PathOf(file), Encoding.UTF8) : new string[0]; }
            catch { return new string[0]; }
        }

        public static void Write(string file, IEnumerable<string> lines)
        {
            if (Program.Demo) return;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(PathOf(file), lines, Encoding.UTF8);
            }
            catch { }
        }

        // One record per line, so newlines and the field separator are escaped.
        public static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "").Replace("|", "\\p"); }

        public static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'n' ? '\n' : c == 'p' ? '|' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    // ---------- AI tools, watched from this PC alone ----------

    class AiTool
    {
        public string Name;
        public double Cpu;
        public bool Working;
        public int Processes;
    }

    // Which AI apps and CLIs are running and whether they're busy (CPU), sampled while the AI page is open.
    class AiToolsMonitor
    {
        static readonly string[][] Tools =
        {
            new[] { "Claude", "claude" }, new[] { "ChatGPT", "chatgpt" }, new[] { "Cursor", "cursor" }, new[] { "Codex", "codex" },
            new[] { "Gemini", "gemini" }, new[] { "Copilot", "copilot", "m365copilot" }, new[] { "Windsurf", "windsurf" },
            new[] { "Grok", "grok" }, new[] { "Ollama", "ollama", "ollama app" }, new[] { "LM Studio", "lm studio", "lmstudio" }
        };
        public event Action<List<AiTool>> Changed;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool active;

        public bool Active
        {
            get { return active; }
            set { active = value; if (value) wake.Set(); }
        }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "AI tools", Priority = ThreadPriority.BelowNormal }.Start(); }

        void Loop()
        {
            var lastCpu = new Dictionary<string, TimeSpan>();
            var lastTime = DateTime.UtcNow;
            while (true)
            {
                if (!active) { wake.WaitOne(); lastCpu.Clear(); continue; }
                var now = DateTime.UtcNow;
                double elapsed = Math.Max(0.1, (now - lastTime).TotalSeconds);
                lastTime = now;
                var result = new List<AiTool>();
                var cpuNow = new Dictionary<string, TimeSpan>();
                try
                {
                    var byName = Process.GetProcesses().GroupBy(p => p.ProcessName.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
                    foreach (var tool in Tools)
                    {
                        var procs = tool.Skip(1).Where(byName.ContainsKey).SelectMany(n => byName[n]).ToList();
                        if (procs.Count == 0) continue;
                        var total = TimeSpan.Zero;
                        foreach (var p in procs) { try { total += p.TotalProcessorTime; } catch { } }
                        cpuNow[tool[0]] = total;
                        TimeSpan before;
                        double cpu = lastCpu.TryGetValue(tool[0], out before)
                            ? Math.Max(0, (total - before).TotalSeconds / elapsed / Environment.ProcessorCount * 100) : 0;
                        result.Add(new AiTool { Name = tool[0], Cpu = cpu, Working = cpu >= 2, Processes = procs.Count });
                    }
                }
                catch { }
                lastCpu = cpuNow;
                var handler = Changed;
                if (handler != null) handler(result);
                wake.WaitOne(1500);
            }
        }
    }

    // ---------- Local dev servers ----------

    class DevServer
    {
        public int Port, Pid;
        public string Process, Project, Tool;
    }

    // Finds dev servers listening on this PC (node, python, bun...) while the Dev page is open.
    class DevServerScanner
    {
        static readonly HashSet<string> DevProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "bun", "deno", "python", "python3", "py", "pythonw", "ruby", "php", "java", "dotnet", "go", "uvicorn", "hugo", "caddy"
        };
        public event Action<List<DevServer>> Changed;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<int, string> commandLines = new Dictionary<int, string>();
        volatile bool active;

        public bool Active
        {
            get { return active; }
            set { active = value; if (value) wake.Set(); }
        }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "Dev servers", Priority = ThreadPriority.BelowNormal }.Start(); }

        public void Refresh() { wake.Set(); }

        // Ends the server and anything it started (npm, watchers).
        public static void Stop(int pid)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        void Loop()
        {
            while (true)
            {
                if (!active) { wake.WaitOne(); continue; }
                var list = new List<DevServer>();
                try
                {
                    var seen = new HashSet<string>();
                    foreach (var entry in Listeners())
                    {
                        int port = entry.Key, pid = entry.Value;
                        if (port < 1024 || !seen.Add(pid + ":" + port)) continue;
                        string name;
                        try { name = Process.GetProcessById(pid).ProcessName; } catch { continue; }
                        if (!DevProcesses.Contains(name)) continue;
                        var cmd = CommandLine(pid);
                        list.Add(new DevServer { Port = port, Pid = pid, Process = name, Project = ProjectOf(cmd), Tool = ToolOf(cmd, name) });
                    }
                }
                catch { }
                var handler = Changed;
                if (handler != null) handler(list.OrderBy(s => s.Port).ToList());
                wake.WaitOne(3000);
            }
        }

        string CommandLine(int pid)
        {
            string cmd;
            if (commandLines.TryGetValue(pid, out cmd)) return cmd;
            cmd = "";
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                    foreach (ManagementObject o in searcher.Get()) cmd = Convert.ToString(o["CommandLine"]);
            }
            catch { }
            commandLines[pid] = cmd;
            return cmd;
        }

        // The project folder: the part of a path just before node_modules, or the script's folder.
        static string ProjectOf(string cmd)
        {
            var m = Regex.Match(cmd, @"([A-Za-z]:\\[^""]*?)\\node_modules\\", RegexOptions.IgnoreCase);
            if (m.Success) return Path.GetFileName(m.Groups[1].Value);
            m = Regex.Match(cmd, @"([A-Za-z]:\\[^""\s]+)\\[^\\""\s]+\.(py|js|mjs|ts|rb|php)\b", RegexOptions.IgnoreCase);
            if (m.Success) return Path.GetFileName(m.Groups[1].Value);
            return "";
        }

        static string ToolOf(string cmd, string process)
        {
            var c = cmd.ToLowerInvariant();
            string[,] tools =
            {
                { "next", "Next.js" }, { "vite", "Vite" }, { "nuxt", "Nuxt" }, { "astro", "Astro" }, { "remix", "Remix" },
                { "webpack", "Webpack" }, { "expo", "Expo" }, { "storybook", "Storybook" }, { "nodemon", "nodemon" },
                { "uvicorn", "Uvicorn" }, { "flask", "Flask" }, { "django", "Django" }, { "manage.py", "Django" }, { "http.server", "Python HTTP" },
                { "rails", "Rails" }, { "artisan", "Laravel" }, { "wrangler", "Wrangler" }, { "vercel", "Vercel" }
            };
            for (int i = 0; i < tools.GetLength(0); i++)
                if (c.Contains(tools[i, 0])) return tools[i, 1];
            return process;
        }

        // Listening TCP sockets (IPv4 and IPv6) with the owning process.
        static IEnumerable<KeyValuePair<int, int>> Listeners()
        {
            foreach (var family in new[] { 2 /* AF_INET */, 23 /* AF_INET6 */ })
            {
                int size = 0;
                Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3 /* OWNER_PID_LISTENER */, 0);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (Native.GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) continue;
                    int count = Marshal.ReadInt32(buffer);
                    int rowSize = family == 2 ? 24 : 56, portOffset = family == 2 ? 8 : 20, pidOffset = family == 2 ? 20 : 52;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = buffer + 4 + i * rowSize;
                        int rawPort = Marshal.ReadInt32(row, portOffset);
                        int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                        yield return new KeyValuePair<int, int>(port, Marshal.ReadInt32(row, pidOffset));
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
    }

    // ---------- Browser downloads as a live activity ----------

    class DownloadProgress
    {
        public bool Active;
        public string Name;
        public long Bytes;
        public double Speed;
    }

    // Watches the Downloads folder for in-progress files (.crdownload, .part...) and reports when
    // they finish (the browser renames them to the real name).
    class DownloadWatcher
    {
        static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp" };
        public event Action<DownloadProgress> Progress;
        public event Action<string> Finished;
        readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        FileSystemWatcher watcher;

        public static bool IsTemp(string path)
        {
            return TempExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        public void Start()
        {
            if (!Directory.Exists(folder)) return;
            try
            {
                watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
                watcher.Renamed += (s, e) => { if (IsTemp(e.OldFullPath) && !IsTemp(e.FullPath)) Done(e.FullPath); };
                // Firefox keeps the final file and deletes the .part when it's done.
                watcher.Deleted += (s, e) =>
                {
                    if (!e.FullPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return;
                    var final = e.FullPath.Substring(0, e.FullPath.Length - 5);
                    if (File.Exists(final)) Done(final);
                };
                watcher.EnableRaisingEvents = true;
            }
            catch { }
            new Thread(Loop) { IsBackground = true, Name = "Downloads", Priority = ThreadPriority.BelowNormal }.Start();
        }

        void Done(string path)
        {
            var handler = Finished;
            if (handler != null) handler(path);
        }

        void Loop()
        {
            string lastFile = null;
            long lastBytes = 0;
            DateTime lastTime = DateTime.UtcNow;
            bool wasActive = false;
            while (true)
            {
                var report = new DownloadProgress();
                try
                {
                    var active = new DirectoryInfo(folder).EnumerateFiles()
                        .Where(f => IsTemp(f.Name) && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 20)
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                    if (active != null)
                    {
                        var now = DateTime.UtcNow;
                        report.Active = true;
                        report.Name = DisplayName(active.Name);
                        report.Bytes = active.Length;
                        if (active.FullName == lastFile) report.Speed = Math.Max(0, (active.Length - lastBytes) / Math.Max(0.1, (now - lastTime).TotalSeconds));
                        lastFile = active.FullName;
                        lastBytes = active.Length;
                        lastTime = now;
                    }
                    else lastFile = null;
                }
                catch { }
                if (report.Active || wasActive)
                {
                    var handler = Progress;
                    if (handler != null) handler(report);
                }
                wasActive = report.Active;
                Thread.Sleep(1000);
            }
        }

        static string DisplayName(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return name.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase) ? "Download" : name;
        }
    }

    // ---------- "call mom tomorrow 5pm" ----------

    static class TodoParser
    {
        static readonly string[] Weekdays = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

        // Pulls a due date/time out of the text and returns what's left as the todo.
        public static string Parse(string input, DateTime now, out DateTime? due)
        {
            string text = " " + input.Trim() + " ";
            DateTime? day = null;
            TimeSpan? time = null;
            Match m;

            m = Regex.Match(text, @"\s+in\s+(\d+)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                char unit = char.ToLowerInvariant(m.Groups[2].Value[0]);
                due = unit == 'm' ? now.AddMinutes(n) : unit == 'h' ? now.AddHours(n) : now.Date.AddDays(n).AddHours(9);
                return Clean(text.Remove(m.Index, m.Length));
            }

            m = Regex.Match(text, @"\s+(today|tonight|tomorrow|tmrw|next week)\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var word = m.Groups[1].Value.ToLowerInvariant();
                day = word == "next week" ? now.Date.AddDays(7) : word.StartsWith("t") && word != "today" && word != "tonight" ? now.Date.AddDays(1) : now.Date;
                if (word == "tonight") time = new TimeSpan(20, 0, 0);
                text = text.Remove(m.Index, m.Length);
            }
            else
            {
                m = Regex.Match(text, @"\s+(on\s+)?(" + string.Join("|", Weekdays) + @")\b", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int target = Array.IndexOf(Weekdays, m.Groups[2].Value.ToLowerInvariant());
                    int ahead = (target - (int)now.DayOfWeek + 7) % 7;
                    day = now.Date.AddDays(ahead == 0 ? 7 : ahead);
                    text = text.Remove(m.Index, m.Length);
                }
            }

            m = Regex.Match(text, @"\s+(at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int h = int.Parse(m.Groups[2].Value) % 12, min = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                if (m.Groups[4].Value.ToLowerInvariant() == "pm") h += 12;
                time = new TimeSpan(h, min, 0);
                text = text.Remove(m.Index, m.Length);
            }
            else
            {
                m = Regex.Match(text, @"\s+(at\s+)?([01]?\d|2[0-3]):([0-5]\d)\b", RegexOptions.IgnoreCase);
                if (!m.Success) m = Regex.Match(text, @"\s+(at)\s+([01]?\d|2[0-3])()\b", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int h = int.Parse(m.Groups[2].Value), min = m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : 0;
                    // "at 5" most likely means 5pm.
                    if (m.Groups[3].Value.Length == 0 && h >= 1 && h <= 7) h += 12;
                    time = new TimeSpan(h, min, 0);
                    text = text.Remove(m.Index, m.Length);
                }
                else if (Regex.IsMatch(text, @"\s+noon\b", RegexOptions.IgnoreCase))
                {
                    time = new TimeSpan(12, 0, 0);
                    text = Regex.Replace(text, @"\s+(at\s+)?noon\b", " ", RegexOptions.IgnoreCase);
                }
            }

            if (day == null && time == null) { due = null; return input.Trim(); }
            var date = (day ?? now.Date) + (time ?? new TimeSpan(9, 0, 0));
            if (day == null && date <= now) date = date.AddDays(1);
            due = date;
            var rest = Clean(text);
            return rest.Length > 0 ? rest : input.Trim();
        }

        static string Clean(string s) { return Regex.Replace(s, @"\s+", " ").Trim(); }

        public static string Describe(DateTime due, DateTime now)
        {
            var culture = CultureInfo.CurrentCulture;
            string time = due.ToString("t", culture);
            if (due.Date == now.Date) return "Today " + time;
            if (due.Date == now.Date.AddDays(1)) return "Tomorrow " + time;
            if (due.Date < now.Date.AddDays(7) && due.Date > now.Date) return due.ToString("dddd", culture) + " " + time;
            return due.ToString("d MMM", culture) + " " + time;
        }
    }

    // ---------- Colour maths for the colour picker ----------

    static class ColorMath
    {
        public static void ToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
        {
            double R = r / 255.0, G = g / 255.0, B = b / 255.0;
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            l = (max + min) / 2;
            if (max == min) { h = s = 0; return; }
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == R) h = (G - B) / d + (G < B ? 6 : 0);
            else if (max == G) h = (B - R) / d + 2;
            else h = (R - G) / d + 4;
            h *= 60;
        }

        public static void FromHsl(double h, double s, double l, out byte r, out byte g, out byte b)
        {
            Func<double, double, double, double> hue = (p, q, t) =>
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1 / 6.0) return p + (q - p) * 6 * t;
                if (t < 1 / 2.0) return q;
                if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
                return p;
            };
            double R, G, B;
            if (s == 0) R = G = B = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q, hh = h / 360;
                R = hue(p, q, hh + 1 / 3.0);
                G = hue(p, q, hh);
                B = hue(p, q, hh - 1 / 3.0);
            }
            r = (byte)Math.Round(R * 255);
            g = (byte)Math.Round(G * 255);
            b = (byte)Math.Round(B * 255);
        }
    }
}
