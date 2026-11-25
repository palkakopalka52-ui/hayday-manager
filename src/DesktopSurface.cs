using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FarmDesk
{
    internal sealed class DesktopSurface
    {
        private const uint MouseDown = 0x0002, MouseUp = 0x0004;
        public const int FrameWidth = 960, FrameHeight = 540;
        private readonly IntPtr handle;
        private readonly int processId;

        public DesktopSurface(PlayerInstance player)
        {
            if (player == null || player.Handle == IntPtr.Zero) throw new CycleStop("Окно Android ещё не готово.");
            handle = player.Handle;
            processId = player.ProcessId;
        }

        public async Task ActivateAsync(CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            ValidateHandle();
            if (IsIconic(handle)) ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (IsForeground()) return;
                await Task.Delay(110, stop).ConfigureAwait(false);
            }
            throw new CycleStop("Выведи окно этого экземпляра BlueStacks на передний план.");
        }

        public Bitmap CaptureClient()
        {
            Rectangle client = ForegroundClient();
            return CopyScreen(client);
        }

        internal VisualRegion DetectGameViewport(FarmCalibration profile)
        {
            using (Bitmap current = Capture(profile.Viewport))
                foreach (string key in new[] { "farm", "farmEnvelope", "shop", "listing", "full", "newspaper" })
                {
                    VisualMarker marker;
                    if (profile.Markers.TryGetValue(key, out marker) && ScreenVision.Matches(current, marker, 17))
                        return profile.Viewport;
                }
            using (Bitmap client = CaptureClient())
                return FindGameViewport(client, profile.Markers["farm"]);
        }

        internal static VisualRegion FindGameViewport(Bitmap client, VisualMarker hud)
        {
            int top = -1, bottom = -1;
            for (int y = 0; y < client.Height; y++)
            {
                int colorful = 0;
                for (int column = 0; column < 20; column++)
                {
                    Color pixel = client.GetPixel(client.Width * (28 + column * 2) / 100, y);
                    int highest = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                    int lowest = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    if (highest >= 70 && highest - lowest > 32) colorful++;
                }
                if (colorful < 11) continue;
                if (top < 0) top = y;
                bottom = y + 1;
            }
            if (top < 0 || bottom - top < 150) return null;

            double best = 14;
            VisualRegion viewport = null;
            Rectangle normalized = hud.Region.ToRectangle(FrameWidth, FrameHeight);
            for (int y = Math.Max(0, top - 5); y <= top + 5; y++)
                for (int end = bottom - 5; end <= Math.Min(client.Height, bottom + 5); end++)
                {
                    int height = end - y;
                    int width = (int)Math.Round(height * 16.0 / 9);
                    for (int x = 0; x <= client.Width - width; x++)
                    {
                        var icon = new Rectangle(x + (int)Math.Round(normalized.X * width / (double)FrameWidth),
                            y + (int)Math.Round(normalized.Y * height / (double)FrameHeight),
                            (int)Math.Round(normalized.Width * width / (double)FrameWidth),
                            (int)Math.Round(normalized.Height * height / (double)FrameHeight));
                        double score = ScreenVision.Distance(client, hud, icon);
                        score += (Math.Abs(y - top) + Math.Abs(end - bottom)) * 0.2;
                        if (score >= best) continue;
                        best = score;
                        viewport = VisualRegion.FromRectangle(new Rectangle(x, y, width, height), client.Width, client.Height);
                    }
                }
            return viewport;
        }

        public Bitmap Capture(VisualRegion crop)
        {
            Rectangle viewport = View(crop);
            using (Bitmap original = CopyScreen(viewport))
            {
                var output = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(output))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(original, new Rectangle(0, 0, FrameWidth, FrameHeight));
                }
                return output;
            }
        }

        public async Task TapAsync(VisualRegion crop, Point point, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            Rectangle viewport = View(crop);
            MoveVerified(viewport, Map(viewport, point));
            SendButton(MouseDown);
            try { await Task.Delay(75, stop).ConfigureAwait(false); }
            finally { SendButton(MouseUp); }
        }

        internal void MovePointer(VisualRegion crop, Point point)
        {
            Rectangle viewport = View(crop);
            MoveVerified(viewport, Map(viewport, point));
        }

        public async Task DragAsync(VisualRegion crop, Point[] route, CancellationToken stop)
        {
            if (route.Length < 2) throw new ArgumentException("Жест требует начальной и конечной точек.");
            stop.ThrowIfCancellationRequested();
            Rectangle viewport = View(crop);
            Point start = Map(viewport, route[0]);
            MoveVerified(viewport, start);
            SendButton(MouseDown);
            try
            {
                await Task.Delay(130, stop).ConfigureAwait(false);
                for (int index = 1; index < route.Length; index++)
                {
                    Point from = Map(viewport, route[index - 1]);
                    Point to = Map(viewport, route[index]);
                    int steps = Math.Max(5, Math.Min(36, (int)(Distance(from, to) / 12)));
                    for (int step = 1; step <= steps; step++)
                    {
                        stop.ThrowIfCancellationRequested();
                        if (View(crop) != viewport) throw new CycleStop("Окно переместилось во время жеста.");
                        var next = new Point(from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps);
                        MoveVerified(viewport, next);
                        await Task.Delay(17, stop).ConfigureAwait(false);
                    }
                    await Task.Delay(26, stop).ConfigureAwait(false);
                }
            }
            finally { SendButton(MouseUp); }
        }

        private Bitmap CopyScreen(Rectangle area)
        {
            if (area.Width < 240 || area.Height < 150)
                throw new CycleStop("Слишком малая видимая область Android.");
            Point[] checkPoints =
            {
                new Point(area.Left + 5, area.Top + 5),
                new Point(area.Right - 5, area.Top + 5),
                new Point(area.Left + 5, area.Bottom - 5),
                new Point(area.Right - 5, area.Bottom - 5),
                new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)
            };
            foreach (Point point in checkPoints) VerifyPoint(point);
            var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                if (!IsForeground()) throw new CycleStop("Фокус ушёл из BlueStacks во время захвата.");
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private Rectangle View(VisualRegion crop)
        {
            Rectangle client = ForegroundClient();
            Rectangle view = crop.ToRectangle(client.Width, client.Height);
            view.Offset(client.Location);
            return view;
        }

        private Rectangle ForegroundClient()
        {
            if (!IsForeground()) throw new CycleStop("Окно Android потеряло фокус.");
            return ClientBounds();
        }

        internal Rectangle ClientBounds()
        {
            ValidateHandle();
            NativeRect bounds;
            NativePoint origin = new NativePoint();
            if (!GetClientRect(handle, out bounds) || !ClientToScreen(handle, ref origin))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Границы окна Android недоступны.");
            return new Rectangle(origin.X, origin.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }

        private void ValidateHandle()
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != processId || !IsWindowVisible(handle)) throw new CycleStop("Окно выбранного экземпляра закрылось.");
        }

        internal bool IsForeground() { return RootOf(GetForegroundWindow()) == handle; }
        private static IntPtr RootOf(IntPtr window) { return GetAncestor(window, 2); }

        private void MoveVerified(Rectangle area, Point point)
        {
            if (!area.Contains(point)) throw new CycleStop("Точка ввода вышла из области Android.");
            VerifyPoint(point);
            if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось переместить указатель.");
        }

        private void VerifyPoint(Point point)
        {
            if (!IsForeground() || RootOf(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y })) != handle)
                throw new CycleStop("Область Android перекрыта или окно потеряло фокус.");
        }

        private static Point Map(Rectangle area, Point point)
        {
            if (point.X < 0 || point.X >= FrameWidth || point.Y < 0 || point.Y >= FrameHeight)
                throw new CycleStop("Калибровка содержит точку за границей Android.");
            return new Point(area.Left + point.X * area.Width / FrameWidth, area.Top + point.Y * area.Height / FrameHeight);
        }

        private static double Distance(Point first, Point last)
        {
            return Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2));
        }

        private static void SendButton(uint flags)
        {
            var input = new Input { Kind = 0, Mouse = new MouseInput { Flags = flags } };
            if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не приняла жест BlueStacks.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Kind; public MouseInput Mouse; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] input, int size);
    }
}
