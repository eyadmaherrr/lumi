// Hand Control: control the PC with hand gestures seen by the webcam.
//   thumbs up / down      volume up / down, repeating while held
//   point right / left    Right / Left arrow
//   pinch                 Space, once per pinch
// Hands are found with Google's MediaPipe models (palm detector + 21-point hand landmarks, Apache 2.0,
// the ONNX conversions from OpenCV's model zoo), run on the CPU with Windows ML. They are downloaded
// once into %APPDATA%\Lumi\models. Frames are processed in memory only; nothing is saved or sent.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Windows.AI.MachineLearning;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT = Windows.Foundation;

namespace Lumi
{
    struct HandPoint
    {
        public double X, Y;
        public HandPoint(double x, double y) { X = x; Y = y; }
    }

    // A rotated square in image pixels: where the hand is, and which way its fingers point.
    struct HandRect
    {
        public double X, Y, Size, Angle;
    }

    // ---------- Finding the hand ----------

    // Runs the two models on one camera frame (BGRA bytes). Keeps following the hand from frame to
    // frame with the landmark model alone, and only runs the palm detector when it has lost it.
    class HandTracker : IDisposable
    {
        const int PalmSize = 192, HandSize = 224;
        readonly LearningModel palmModel, handModel;
        readonly LearningModelSession palmSession, handSession;
        readonly double[] anchorX = new double[2016], anchorY = new double[2016];
        readonly float[] palmInput = new float[PalmSize * PalmSize * 3], handInput = new float[HandSize * HandSize * 3];
        bool tracking;
        HandRect roi;

        public HandTracker(string palmPath, string handPath)
        {
            palmModel = LearningModel.LoadFromFilePath(palmPath);
            handModel = LearningModel.LoadFromFilePath(handPath);
            palmSession = new LearningModelSession(palmModel, new LearningModelDevice(LearningModelDeviceKind.Cpu));
            handSession = new LearningModelSession(handModel, new LearningModelDevice(LearningModelDeviceKind.Cpu));

            // SSD anchors of the palm detector: a 24x24 grid with 2 anchors per cell, then 12x12 with 6.
            int i = 0;
            foreach (var layer in new[] { new[] { 24, 2 }, new[] { 12, 6 } })
                for (int y = 0; y < layer[0]; y++)
                    for (int x = 0; x < layer[0]; x++)
                        for (int k = 0; k < layer[1]; k++, i++)
                        {
                            anchorX[i] = (x + 0.5) / layer[0];
                            anchorY[i] = (y + 0.5) / layer[0];
                        }
        }

        public HandRect Roi { get { return roi; } }

        // The 21 landmarks in image pixels, or null when there's no hand.
        public HandPoint[] Process(byte[] bgra, int width, int height)
        {
            if (!tracking)
            {
                HandRect palm;
                if (!DetectPalm(bgra, width, height, out palm)) return null;
                roi = palm;
                tracking = true;
            }
            var points = Landmarks(bgra, width, height, roi);
            if (points == null)
            {
                tracking = false;
                return null;
            }
            roi = RectFromLandmarks(points);
            return points;
        }

        bool DetectPalm(byte[] bgra, int width, int height, out HandRect rect)
        {
            rect = new HandRect();
            // Letterbox the frame into the square input, keeping its shape.
            double scale = (double)PalmSize / Math.Max(width, height);
            double padX = (PalmSize - width * scale) / 2, padY = (PalmSize - height * scale) / 2;
            int o = 0;
            for (int v = 0; v < PalmSize; v++)
                for (int u = 0; u < PalmSize; u++, o += 3)
                    Sample(bgra, width, height, (u + 0.5 - padX) / scale - 0.5, (v + 0.5 - padY) / scale - 0.5, palmInput, o);

            var outputs = Evaluate(palmSession, palmModel, palmInput, PalmSize);
            var boxes = outputs[0];
            var scores = outputs[1];
            int best = -1;
            double bestScore = 0.5;
            for (int i = 0; i < scores.Length; i++)
            {
                double s = Sigmoid(scores[i]);
                if (s > bestScore) { bestScore = s; best = i; }
            }
            if (best < 0) return false;

            int b = best * 18;
            Func<double, double, HandPoint> toImage = (nx, ny) =>
                new HandPoint((nx * PalmSize - padX) / scale, (ny * PalmSize - padY) / scale);
            var center = toImage(boxes[b] / PalmSize + anchorX[best], boxes[b + 1] / PalmSize + anchorY[best]);
            double w = boxes[b + 2] / scale, h = boxes[b + 3] / scale;
            // Keypoint 0 is the wrist, 2 the base of the middle finger.
            var wrist = toImage(boxes[b + 4] / PalmSize + anchorX[best], boxes[b + 5] / PalmSize + anchorY[best]);
            var middle = toImage(boxes[b + 8] / PalmSize + anchorX[best], boxes[b + 9] / PalmSize + anchorY[best]);
            double angle = Angle(wrist, middle);

            // The palm box grows into a box for the whole hand, shifted towards the fingers.
            rect.X = center.X + 0.5 * h * Math.Sin(angle);
            rect.Y = center.Y - 0.5 * h * Math.Cos(angle);
            rect.Size = Math.Max(w, h) * 2.6;
            rect.Angle = angle;
            return true;
        }

        HandPoint[] Landmarks(byte[] bgra, int width, int height, HandRect r)
        {
            double cos = Math.Cos(r.Angle), sin = Math.Sin(r.Angle);
            int o = 0;
            for (int v = 0; v < HandSize; v++)
                for (int u = 0; u < HandSize; u++, o += 3)
                {
                    double lx = ((u + 0.5) / HandSize - 0.5) * r.Size, ly = ((v + 0.5) / HandSize - 0.5) * r.Size;
                    Sample(bgra, width, height, r.X + lx * cos - ly * sin - 0.5, r.Y + lx * sin + ly * cos - 0.5, handInput, o);
                }

            var outputs = Evaluate(handSession, handModel, handInput, HandSize);
            double presence = outputs[1][0];
            if (presence < 0 || presence > 1) presence = Sigmoid(presence);
            if (presence < 0.6) return null;

            var marks = outputs[0];
            var points = new HandPoint[21];
            for (int i = 0; i < 21; i++)
            {
                double lx = (marks[i * 3] / HandSize - 0.5) * r.Size, ly = (marks[i * 3 + 1] / HandSize - 0.5) * r.Size;
                points[i] = new HandPoint(r.X + lx * cos - ly * sin, r.Y + lx * sin + ly * cos);
            }
            return points;
        }

        // The next frame's crop: around the landmarks, turned to the hand, with room to move.
        static HandRect RectFromLandmarks(HandPoint[] p)
        {
            double angle = Angle(p[0], p[9]);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var q in p)
            {
                double x = q.X * cos + q.Y * sin, y = -q.X * sin + q.Y * cos;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            double w = maxX - minX, h = maxY - minY;
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2 - 0.1 * h;
            return new HandRect { X = cx * cos - cy * sin, Y = cx * sin + cy * cos, Size = Math.Max(w, h) * 2.0, Angle = angle };
        }

        // Rotation that turns the wrist-to-middle-finger direction straight up.
        static double Angle(HandPoint wrist, HandPoint middle)
        {
            double a = Math.PI / 2 - Math.Atan2(-(middle.Y - wrist.Y), middle.X - wrist.X);
            return a - 2 * Math.PI * Math.Floor((a + Math.PI) / (2 * Math.PI));
        }

        static double Sigmoid(double x) { return 1 / (1 + Math.Exp(-x)); }

        // Bilinear sample at (x, y) into RGB floats 0..1; black outside the frame.
        static void Sample(byte[] bgra, int width, int height, double x, double y, float[] dst, int o)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            double r = 0, g = 0, b = 0;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int px = x0 + dx, py = y0 + dy;
                    if (px < 0 || py < 0 || px >= width || py >= height) continue;
                    double wgt = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                    int i = (py * width + px) * 4;
                    b += bgra[i] * wgt; g += bgra[i + 1] * wgt; r += bgra[i + 2] * wgt;
                }
            dst[o] = (float)(r / 255); dst[o + 1] = (float)(g / 255); dst[o + 2] = (float)(b / 255);
        }

        static float[][] Evaluate(LearningModelSession session, LearningModel model, float[] input, int size)
        {
            var binding = new LearningModelBinding(session);
            binding.Bind(model.InputFeatures.First().Name, TensorFloat.CreateFromArray(new long[] { 1, size, size, 3 }, input));
            var op = session.EvaluateAsync(binding, "");
            while (op.Status == WinRT.AsyncStatus.Started) Thread.Sleep(1);
            var result = op.GetResults();
            // Outputs are named Identity, Identity_1, ...; return them in that order.
            return model.OutputFeatures.Select(f => Enumerable.ToArray(((TensorFloat)result.Outputs[f.Name]).GetAsVectorView())).ToArray();
        }

        public void Dispose()
        {
            palmSession.Dispose();
            handSession.Dispose();
            palmModel.Dispose();
            handModel.Dispose();
        }
    }

    // ---------- Reading gestures ----------

    enum Gesture { None, ThumbsUp, ThumbsDown, PointRight, PointLeft, Pinch }

    // Turns landmarks into a gesture, and decides when a held gesture should fire. "Right" and "left"
    // are from your point of view (the camera sees you mirrored).
    class GestureReader
    {
        public const int HoldMs = 250, PinchHoldMs = 90, RepeatMs = 170;
        Gesture held = Gesture.None;
        DateTime heldSince, lastFired;
        bool firedOnce, pinching;

        public Gesture Current { get { return held; } }

        static double D(HandPoint a, HandPoint b) { return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }

        // Finger tips 8, 12, 16, 20 with their middle joints 6, 10, 14, 18; 0 is the wrist.
        static bool Folded(HandPoint[] p, int tip) { return D(p[0], p[tip]) < D(p[0], p[tip - 2]) * 1.05; }
        static bool Extended(HandPoint[] p, int tip, double palm)
        {
            return D(p[0], p[tip]) > D(p[0], p[tip - 2]) * 1.12 && D(p[tip - 3], p[tip]) > 0.55 * palm;
        }

        public Gesture Classify(HandPoint[] p, int width, int height)
        {
            if (p == null) { pinching = false; return Gesture.None; }
            double palm = D(p[0], p[9]);
            // Only a hand that's held up, whole and in view: not one cut off at the edge, resting on the
            // keyboard at the bottom of the picture, or tiny in the background.
            double margin = 0.015 * Math.Max(width, height);
            if (p.Any(q => q.X < margin || q.Y < margin || q.X > width - margin || q.Y > height - margin) || palm < 0.07 * height)
            {
                pinching = false;
                return Gesture.None;
            }

            // Pinch: thumb and index tips together with the other fingers relaxed or open (a fist doesn't
            // count). Some slack before it lets go, so one pinch is one press.
            int open = new[] { 12, 16, 20 }.Count(tip => D(p[0], p[tip]) > D(p[0], p[tip - 2]));
            double pinch = D(p[4], p[8]) / palm;
            pinching = pinching ? pinch < 0.38 : pinch < 0.22 && open >= 2;
            if (pinching) return Gesture.Pinch;

            bool fist = Folded(p, 8) && Folded(p, 12) && Folded(p, 16) && Folded(p, 20);
            if (fist)
            {
                // Thumbs up/down: a straight thumb, pointing up or down, and the hand's highest or lowest point.
                double tx = p[4].X - p[2].X, ty = p[4].Y - p[2].Y;
                if (D(p[2], p[4]) > 0.45 * palm && Math.Abs(ty) > Math.Abs(tx))
                {
                    double others = ty < 0 ? p.Where((q, i) => i != 4 && i != 3).Min(q => q.Y) : p.Where((q, i) => i != 4 && i != 3).Max(q => q.Y);
                    if (ty < 0 && p[4].Y < others) return Gesture.ThumbsUp;
                    if (ty > 0 && p[4].Y > others) return Gesture.ThumbsDown;
                }
                return Gesture.None;
            }

            // Pointing: only the index finger out, held level. Diagonals are ignored on purpose.
            if (Extended(p, 8, palm) && Folded(p, 12) && Folded(p, 16) && Folded(p, 20))
            {
                double dx = p[8].X - p[5].X, dy = p[8].Y - p[5].Y;
                if (Math.Abs(dx) > 1.7 * Math.Abs(dy)) return dx < 0 ? Gesture.PointRight : Gesture.PointLeft;
            }
            return Gesture.None;
        }

        // Feed one frame's gesture; returns the gesture to act on now, or None.
        public Gesture Update(Gesture g, DateTime now)
        {
            if (g != held)
            {
                held = g;
                heldSince = now;
                firedOnce = false;
                return Gesture.None;
            }
            if (g == Gesture.None) return Gesture.None;
            double heldMs = (now - heldSince).TotalMilliseconds;
            if (!firedOnce)
            {
                if (heldMs < (g == Gesture.Pinch ? PinchHoldMs : HoldMs)) return Gesture.None;
                firedOnce = true;
                lastFired = now;
                return g;
            }
            // Volume keeps going while you hold the thumb; the rest fire once per gesture.
            if ((g == Gesture.ThumbsUp || g == Gesture.ThumbsDown) && (now - lastFired).TotalMilliseconds >= RepeatMs)
            {
                lastFired = now;
                return g;
            }
            return Gesture.None;
        }

        public static string Name(Gesture g)
        {
            switch (g)
            {
                case Gesture.ThumbsUp: return "Thumbs up";
                case Gesture.ThumbsDown: return "Thumbs down";
                case Gesture.PointRight: return "Pointing right";
                case Gesture.PointLeft: return "Pointing left";
                case Gesture.Pinch: return "Pinch";
                default: return "";
            }
        }
    }

    // ---------- The camera loop ----------

    class HandFrame
    {
        public HandPoint[] Points;   // null when no hand is in view
        public int Width, Height;
        public Gesture Gesture;
    }

    class HandControl
    {
        static readonly string ModelFolder = Store.PathOf("models");
        static readonly string[][] Models =
        {
            new[] { "palm_detection_mediapipe_2023feb.onnx", "https://huggingface.co/opencv/palm_detection_mediapipe/resolve/main/palm_detection_mediapipe_2023feb.onnx", "78ff51c38496b7fc8b8ebdb6cc8c1abb02fa6c38427c6848254cdaba57fcce7c" },
            new[] { "handpose_estimation_mediapipe_2023feb.onnx", "https://huggingface.co/opencv/handpose_estimation_mediapipe/resolve/main/handpose_estimation_mediapipe_2023feb.onnx", "db0898ae717b76b075d9bf563af315b29562e11f8df5027a1ef07b02bef6d81c" }
        };
        const int FrameMs = 45; // about 20 frames a second is plenty for gestures

        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        Thread worker;
        volatile bool running;

        public bool Running { get { return running; } }
        public bool SendKeys = true;                  // off only for testing
        public string Status { get; private set; }
        public bool Problem { get; private set; }   // Status describes something that stopped it

        public event Action StatusChanged;          // on the camera thread
        public event Action<HandFrame> FrameRead;    // on the camera thread
        public event Action<Gesture> Fired;          // on the camera thread

        public HandControl() { Status = "Off"; }

        public void Start()
        {
            if (running) return;
            running = true;
            Problem = false;
            worker = new Thread(Run) { IsBackground = true, Name = "Hand Control" };
            worker.Start();
        }

        public void Stop()
        {
            running = false;
        }

        void SetStatus(string text, bool problem)
        {
            Status = text;
            Problem = problem;
            var h = StatusChanged;
            if (h != null) h();
        }

        void Run()
        {
            MediaCapture capture = null;
            MediaFrameReader reader = null;
            HandTracker tracker = null;
            try
            {
                if (!ModelsReady())
                {
                    SetStatus("Downloading hand models (8 MB)…", false);
                    DownloadModels();
                }
                if (!running) return;
                try { tracker = new HandTracker(Path.Combine(ModelFolder, Models[0][0]), Path.Combine(ModelFolder, Models[1][0])); }
                catch (Exception) { throw new HandControlException("This version of Windows can't run the hand models. Windows 11 is needed."); }

                SetStatus("Starting the camera…", false);
                reader = OpenCamera(out capture);
                SetStatus("Looking for your hand", false);

                var reading = new GestureReader();
                byte[] pixels = null;
                Windows.Storage.Streams.Buffer buffer = null;
                bool hadHand = false;
                while (running)
                {
                    var started = DateTime.Now;
                    using (var frame = reader.TryAcquireLatestFrame())
                    {
                        var bitmap = frame == null || frame.VideoMediaFrame == null ? null : frame.VideoMediaFrame.SoftwareBitmap;
                        if (bitmap != null)
                        {
                            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8) bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
                            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
                            if (pixels == null || pixels.Length != w * h * 4)
                            {
                                pixels = new byte[w * h * 4];
                                buffer = new Windows.Storage.Streams.Buffer((uint)pixels.Length);
                            }
                            bitmap.CopyToBuffer(buffer);
                            using (var data = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) data.ReadBytes(pixels);

                            var points = tracker.Process(pixels, w, h);
                            var gesture = reading.Classify(points, w, h);
                            var fire = reading.Update(gesture, DateTime.Now);
                            if (fire != Gesture.None)
                            {
                                if (SendKeys) Act(fire);
                                var f = Fired;
                                if (f != null) f(fire);
                            }

                            if ((points != null) != hadHand)
                            {
                                hadHand = points != null;
                                SetStatus(hadHand ? "Watching your hand" : "Looking for your hand", false);
                            }
                            var h1 = FrameRead;
                            if (h1 != null) h1(new HandFrame { Points = points, Width = w, Height = h, Gesture = gesture });
                        }
                    }
                    int rest = FrameMs - (int)(DateTime.Now - started).TotalMilliseconds;
                    Thread.Sleep(Math.Max(5, rest));
                }
                SetStatus("Off", false);
            }
            catch (HandControlException e) { running = false; SetStatus(e.Message, true); }
            catch (UnauthorizedAccessException) { running = false; SetStatus("Camera access is off. Allow desktop apps to use the camera in Settings.", true); }
            catch (WebException) { running = false; SetStatus("Couldn't download the hand models. Check your internet and try again.", true); }
            catch (Exception e) { running = false; SetStatus("Stopped: " + e.Message, true); }
            finally
            {
                if (reader != null) try { WaitAction(reader.StopAsync()); reader.Dispose(); } catch { }
                if (capture != null) try { capture.Dispose(); } catch { }
                if (tracker != null) try { tracker.Dispose(); } catch { }
                var h2 = FrameRead;
                if (h2 != null) h2(new HandFrame());
            }
        }

        static MediaFrameReader OpenCamera(out MediaCapture capture)
        {
            var groups = WinRt.Wait(MediaFrameSourceGroup.FindAllAsync());
            var group = groups.FirstOrDefault(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));
            if (group == null) throw new HandControlException("No camera found.");

            // Ask for the camera to ourselves so we can pick a small size; share it if another app has it.
            capture = null;
            bool exclusive = false;
            foreach (var mode in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
            {
                var attempt = new MediaCapture();
                try
                {
                    WaitAction(attempt.InitializeAsync(new MediaCaptureInitializationSettings
                    {
                        SourceGroup = group,
                        SharingMode = mode,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                        StreamingCaptureMode = StreamingCaptureMode.Video
                    }));
                    capture = attempt;
                    exclusive = mode == MediaCaptureSharingMode.ExclusiveControl;
                    break;
                }
                catch (UnauthorizedAccessException) { attempt.Dispose(); throw; }
                catch (Exception) { attempt.Dispose(); }
            }
            if (capture == null) throw new HandControlException("The camera is busy. Close other apps using it (Zoom, Teams, Camera) and try again.");

            var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (exclusive)
            {
                // The smallest format that's still at least 480 wide and smooth enough.
                var format = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width >= 480 && f.FrameRate.Denominator > 0 && (double)f.FrameRate.Numerator / f.FrameRate.Denominator >= 14)
                    .OrderBy(f => f.VideoFormat.Width)
                    .ThenByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                    .FirstOrDefault();
                if (format != null) try { WaitAction(source.SetFormatAsync(format)); } catch { }
            }
            var reader = WinRt.Wait(capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8));
            var status = WinRt.Wait(reader.StartAsync());
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.Dispose();
                throw new HandControlException(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    ? "The camera is busy. Close other apps using it and try again."
                    : "The camera couldn't start (" + status + ").");
            }
            return reader;
        }

        static void Act(Gesture g)
        {
            switch (g)
            {
                case Gesture.ThumbsUp: Press(0xAF, false); break;     // volume up
                case Gesture.ThumbsDown: Press(0xAE, false); break;   // volume down
                case Gesture.PointRight: Press(0x27, true); break;    // right arrow
                case Gesture.PointLeft: Press(0x25, true); break;     // left arrow
                case Gesture.Pinch: Press(0x20, false); break;        // space
            }
        }

        static void Press(byte vk, bool extended)
        {
            uint ext = extended ? 1u : 0u;
            keybd_event(vk, 0, ext, UIntPtr.Zero);
            keybd_event(vk, 0, ext | 2, UIntPtr.Zero);
        }

        // ---------- Models ----------

        static bool ModelsReady()
        {
            return Models.All(m => File.Exists(Path.Combine(ModelFolder, m[0])));
        }

        static void DownloadModels()
        {
            Directory.CreateDirectory(ModelFolder);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            foreach (var m in Models)
            {
                string path = Path.Combine(ModelFolder, m[0]);
                if (File.Exists(path)) continue;
                byte[] data;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "Lumi";
                    data = web.DownloadData(m[1]);
                }
                string hash;
                using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(data).Select(x => x.ToString("x2")));
                if (hash != m[2]) throw new HandControlException("The downloaded hand model didn't match. Try again later.");
                if (File.Exists(path + ".part")) File.Delete(path + ".part");
                File.WriteAllBytes(path + ".part", data);
                File.Move(path + ".part", path);
            }
        }

        static void WaitAction(WinRT.IAsyncAction op)
        {
            while (op.Status == WinRT.AsyncStatus.Started) Thread.Sleep(5);
            op.GetResults();
        }
    }

    class HandControlException : Exception
    {
        public HandControlException(string message) : base(message) { }
    }
}
