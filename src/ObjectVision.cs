using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace FarmDesk
{
    internal static class ObjectVision
    {
        public static Rectangle? Find(Bitmap frame, VisualMarker marker, Rectangle expected, int radius, CancellationToken stop,
            string shape = null)
        {
            string worker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "object-vision.py");
            if (!File.Exists(worker) || marker == null || marker.Rgb == null || marker.Rgb.Length != 64)
                return null;
            string file = Path.Combine(Path.GetTempPath(), "farm-vision-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                stop.ThrowIfCancellationRequested();
                frame.Save(file, ImageFormat.Png);
                string arguments = String.Format(CultureInfo.InvariantCulture,
                    "-3.12 -X utf8 \"{0}\" \"{1}\" {2} {3} {4} {5} {6} {7}", worker, file,
                    expected.X, expected.Y, expected.Width, expected.Height, radius,
                    String.Join(",", marker.Rgb.Select(value => value.ToString(CultureInfo.InvariantCulture))));
                if (shape != null) arguments += " " + shape;
                using (var process = Process.Start(new ProcessStartInfo("py.exe", arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                }))
                {
                    var timeout = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(100))
                        {
                            stop.ThrowIfCancellationRequested();
                            if (timeout.Elapsed.TotalSeconds > 4) return null;
                        }
                        int x, y;
                        string[] result = process.StandardOutput.ReadToEnd().Trim().Split(' ');
                        if (process.ExitCode != 0 || result.Length != 2 ||
                            !Int32.TryParse(result[0], out x) || !Int32.TryParse(result[1], out y)) return null;
                        var found = new Rectangle(x, y, expected.Width, expected.Height);
                        if (shape != null) return new Rectangle(0, 0, frame.Width, frame.Height).Contains(found) ? found : (Rectangle?)null;
                        double score = ScreenVision.Distance(frame, marker, found);
                        return score <= 19 && score <= ScreenVision.Distance(frame, marker, expected) ? found : (Rectangle?)null;
                    }
                    finally { if (!process.HasExited) process.Kill(); }
                }
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
    }
}
