using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FarmDesk
{
    internal sealed class NumberReader
    {
        private const int CropPadding = 12;
        private static readonly int[] ContextHeights = { 24, 42, 48 };
        private static readonly int[] QuantityContextHeights = { 24, 30, 42, 48 };
        private static readonly int[] CapacityScales = { 2, 3, 4 };
        private OcrEngine engine;

        public int Read(Bitmap screenshot, VisualRegion region, CancellationToken stop, bool quantity = false)
        {
            stop.ThrowIfCancellationRequested();
            Rectangle area = region.ToRectangle(screenshot.Width, screenshot.Height);
            if (area.Width > 360 || area.Height > 180)
                throw new CycleStop("Выдели только цифры счётчика.");

            using (Bitmap enlarged = Enlarge(screenshot, area, 3))
            {
                int value;
                if (TryNumber(RecognizeBitmap(enlarged, stop), quantity, out value)) return value;
                if (TryContext(enlarged, quantity, quantity, stop, out value)) return value;
                if (TryContext(enlarged, quantity, !quantity, stop, out value)) return value;
                throw new CycleStop("Число на счётчике не распознано.");
            }
        }

        public Tuple<int, int> ReadCapacity(Bitmap screenshot, VisualRegion region, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            Rectangle area = region.ToRectangle(screenshot.Width, screenshot.Height);
            if (area.Width > 360 || area.Height > 180)
                throw new CycleStop("Выдели только строку заполненности силоса.");

            Tuple<int, int> capacity = null;
            if (TryCapacityScales(screenshot, area, false, stop, ref capacity)) return capacity;
            if (TryCapacityScales(screenshot, area, true, stop, ref capacity)) return capacity;
            throw new CycleStop("Не удалось подтвердить заполненность силоса.");
        }

        private bool TryCapacityScales(Bitmap screenshot, Rectangle area, bool whiteFill,
            CancellationToken stop, ref Tuple<int, int> capacity)
        {
            int readings = 0;
            foreach (int scale in CapacityScales)
            {
                stop.ThrowIfCancellationRequested();
                using (Bitmap enlarged = Enlarge(screenshot, area, scale))
                using (Bitmap ink = IsolateDigits(enlarged, whiteFill))
                {
                    int used, maximum;
                    if (!TryCapacity(RecognizeBitmap(ink, stop), out used, out maximum)) continue;
                    if (maximum == 0 || used > maximum)
                        throw new CycleStop("Заполненность силоса превышает допустимое значение.");
                    if (capacity != null && (capacity.Item1 != used || capacity.Item2 != maximum))
                        throw new CycleStop("Показания заполненности силоса неоднозначны.");
                    capacity = Tuple.Create(used, maximum);
                    readings++;
                }
            }
            return readings >= 2;
        }

        private static bool TryCapacity(string text, out int used, out int maximum)
        {
            used = maximum = 0;
            int slash = text.IndexOf('/');
            if (slash < 0 || slash != text.LastIndexOf('/')) return false;

            Match match = Regex.Match(text, @"(?:^|[ \u00a0:])([0-9]+(?:[ \u00a0][0-9]{3})*)[ \u00a0]*/[ \u00a0]*([0-9]+(?:[ \u00a0][0-9]{3})*)$");
            if (!match.Success) return false;
            string label = text.Substring(0, match.Index).TrimEnd();
            if (label.Length > 0 && (!label.Any(Char.IsLetter) || Regex.IsMatch(label, @"(?:^|\s)[0-9.,+\-−]+$")))
                return false;

            return TryNumber(match.Groups[1].Value, false, out used) &&
                TryNumber(match.Groups[2].Value, false, out maximum);
        }

        private static Bitmap Enlarge(Bitmap screenshot, Rectangle area, int scale)
        {
            int width = area.Width * scale;
            int height = area.Height * scale;
            var enlarged = new Bitmap(width + CropPadding * 2, height + CropPadding * 2);
            using (Graphics graphics = Graphics.FromImage(enlarged))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(screenshot, new Rectangle(CropPadding, CropPadding, width, height), area, GraphicsUnit.Pixel);
            }
            return enlarged;
        }

        private static bool TryNumber(string text, bool quantity, out int value)
        {
            value = 0;
            if (quantity) text = Regex.Replace(text, @"\s*[xXхХ×]$", "");
            return Regex.IsMatch(text, @"^[0-9]+(?:[ \u00a0][0-9]{3})*$") &&
                Int32.TryParse(text.Replace(" ", "").Replace("\u00a0", ""), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value) &&
                (quantity ? value >= 1 && value <= 10 : value <= 99999);
        }

        private bool TryContext(Bitmap source, bool quantity, bool whiteFill, CancellationToken stop, out int value)
        {
            value = 0;
            stop.ThrowIfCancellationRequested();
            using (Bitmap ink = whiteFill ? IsolateDigits(source, true) : IsolateOutlinedDigits(source))
            {
                Rectangle area = InkBounds(ink);
                if (area.Width < 3 || area.Height < 3) return false;

                // Windows OCR can drop isolated digits or interpret them as letters. Only
                // parse text between recognized context markers, with agreement at two scales.
                string pattern = whiteFill ? @"^73\s*(.+?)\s*58$" : @"^Value\s+(.+?)\s+Units$";
                int readings = 0;
                foreach (int height in quantity ? QuantityContextHeights : ContextHeights)
                using (Bitmap page = WithTextContext(ink, area, height, whiteFill))
                {
                    Match match = Regex.Match(RecognizeBitmap(page, stop), pattern);
                    int candidate;
                    if (!match.Success || !TryNumber(match.Groups[1].Value.Trim(), quantity, out candidate)) continue;
                    if (readings != 0 && value != candidate) throw new CycleStop("Показания счётчика неоднозначны.");
                    value = candidate;
                    readings++;
                }
                return readings >= 2;
            }
        }

        private string RecognizeBitmap(Bitmap bitmap, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            using (var buffer = new MemoryStream())
            {
                bitmap.Save(buffer, ImageFormat.Png);
                return Recognize(buffer.ToArray(), stop);
            }
        }

        private static Bitmap IsolateOutlinedDigits(Bitmap source)
        {
            return IsolateDigits(source, false);
        }

        private static Bitmap IsolateDigits(Bitmap source, bool whiteFill)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(result)) graphics.Clear(Color.White);
            for (int y = CropPadding; y < source.Height - CropPadding; y++)
                for (int x = CropPadding; x < source.Width - CropPadding; x++)
                {
                    Color color = source.GetPixel(x, y);
                    int lowest = Math.Min(color.R, Math.Min(color.G, color.B));
                    int highest = Math.Max(color.R, Math.Max(color.G, color.B));
                    int chroma = highest - lowest;
                    bool foreground = whiteFill ? lowest >= 185 && chroma <= 18 : highest < 170 && chroma <= 72;
                    if (foreground) result.SetPixel(x, y, Color.Black);
                }
            return result;
        }

        private static Rectangle InkBounds(Bitmap ink)
        {
            int left = ink.Width;
            int top = ink.Height;
            int right = 0;
            int bottom = 0;
            for (int y = CropPadding; y < ink.Height - CropPadding; y++)
                for (int x = CropPadding; x < ink.Width - CropPadding; x++)
                {
                    if (ink.GetPixel(x, y).R != 0) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            return right > left ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
        }

        private static Bitmap WithTextContext(Bitmap ink, Rectangle area, int height, bool digits)
        {
            const int padding = 26;
            string prefix = digits ? "73" : "Value";
            string suffix = digits ? "58" : "Units";
            int gap = digits ? Math.Max(2, height / 10) : height / 2;
            int width = Math.Max(1, (int)Math.Round(area.Width * (double)height / area.Height));
            using (var font = new Font("Arial", height * 1.33f, digits ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sizingBitmap = new Bitmap(1, 1))
            using (Graphics sizing = Graphics.FromImage(sizingBitmap))
            {
                StringFormat format = StringFormat.GenericTypographic;
                int left = (int)Math.Ceiling(sizing.MeasureString(prefix, font, Int32.MaxValue, format).Width) + gap;
                int right = (int)Math.Ceiling(sizing.MeasureString(suffix, font, Int32.MaxValue, format).Width) + gap;
                var page = new Bitmap(width + padding * 2 + left + right, height + padding * 2);
                using (Graphics graphics = Graphics.FromImage(page))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(ink, new Rectangle(padding + left, padding, width, height), area, GraphicsUnit.Pixel);
                    float y = (page.Height - font.GetHeight()) / 2;
                    graphics.DrawString(prefix, font, Brushes.Black, new PointF(padding, y), format);
                    graphics.DrawString(suffix, font, Brushes.Black, new PointF(padding + left + width + gap, y), format);
                }
                return page;
            }
        }

        private string Recognize(byte[] bytes, CancellationToken stop)
        {
            if (engine == null)
            {
                var languages = OcrEngine.AvailableRecognizerLanguages;
                var language = languages.FirstOrDefault(item => item.LanguageTag == "en-US") ?? languages.FirstOrDefault();
                if (language == null) throw new CycleStop("Windows OCR не нашёл установленный язык распознавания.");
                engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine == null) throw new CycleStop("Не удалось запустить Windows OCR.");
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            using (var stream = new InMemoryRandomAccessStream())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(7));
                try
                {
                    using (var writer = new DataWriter(stream))
                    {
                        writer.WriteBytes(bytes);
                        Await(writer.StoreAsync(), timeout.Token);
                        writer.DetachStream();
                    }
                    stream.Seek(0);
                    var decoder = Await(BitmapDecoder.CreateAsync(stream), timeout.Token);
                    using (var bitmap = Await(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore), timeout.Token))
                        return Await(engine.RecognizeAsync(bitmap), timeout.Token).Text.Trim();
                }
                catch (OperationCanceledException)
                {
                    stop.ThrowIfCancellationRequested();
                    throw new CycleStop("Windows OCR не ответил за семь секунд.");
                }
            }
        }

        private static T Await<T>(IAsyncOperation<T> operation, CancellationToken stop)
        {
            while (operation.Status == AsyncStatus.Started)
            {
                if (!stop.WaitHandle.WaitOne(15)) continue;
                operation.Cancel();
                stop.ThrowIfCancellationRequested();
            }
            return operation.GetResults();
        }
    }
}
