using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace FarmDesk
{
    public sealed class VisualRegion
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public static VisualRegion FromRectangle(Rectangle area, int width, int height)
        {
            return new VisualRegion
            {
                X = (double)area.X / width, Y = (double)area.Y / height,
                Width = (double)area.Width / width, Height = (double)area.Height / height
            };
        }

        public Rectangle ToRectangle(int width, int height)
        {
            var area = new Rectangle((int)Math.Round(X * width), (int)Math.Round(Y * height),
                (int)Math.Round(Width * width), (int)Math.Round(Height * height));
            if (area.Width < 3 || area.Height < 3 || !new Rectangle(0, 0, width, height).Contains(area))
                throw new CycleStop("Область калибровки выходит за видимую рамку.");
            return area;
        }
    }

    public sealed class VisualMarker
    {
        public VisualRegion Region { get; set; }
        public int[] Rgb { get; set; }
    }

    public sealed class FieldArea
    {
        public VisualRegion Left { get; set; }
        public VisualRegion Top { get; set; }
        public VisualRegion Bottom { get; set; }
        public int Step { get; set; }

        internal Point[] Sweep(int width, int height)
        {
            if (Left == null || Top == null || Bottom == null || Step < 10 || Step > 80)
                throw new CycleStop("Отметь три угла участка и задай шаг от 10 до 80 пикселей.");
            Point left = ScreenVision.Center(Left.ToRectangle(width, height));
            Point top = ScreenVision.Center(Top.ToRectangle(width, height));
            Point bottom = ScreenVision.Center(Bottom.ToRectangle(width, height));
            var right = new Point(top.X + bottom.X - left.X, top.Y + bottom.Y - left.Y);
            if (top.X <= left.X || bottom.X <= left.X || top.Y >= left.Y || bottom.Y <= left.Y ||
                !new Rectangle(0, 0, width, height).Contains(right))
                throw new CycleStop("Отметь левый, верхний и нижний углы поля в этом порядке.");

            double pitch = Step * (double)width / DesktopSurface.FrameWidth;
            int columns = Math.Max(1, (int)Math.Round((top.X - left.X) / pitch));
            int rows = Math.Max(1, (int)Math.Round((bottom.X - left.X) / pitch));
            if (columns * rows > 120) throw new CycleStop("Увеличь шаг прохода или уменьши область поля.");

            var points = new List<Point>();
            for (int column = 0; column < columns; column++)
            {
                double along = (column + 0.5) / columns;
                for (int index = 0; index < rows; index++)
                {
                    int row = column % 2 == 0 ? index : rows - 1 - index;
                    double across = (row + 0.5) / rows;
                    points.Add(new Point(
                        (int)Math.Round(left.X + along * (top.X - left.X) + across * (bottom.X - left.X)),
                        (int)Math.Round(left.Y + along * (top.Y - left.Y) + across * (bottom.Y - left.Y))));
                }
            }
            return points.ToArray();
        }
    }

    public sealed class FarmCalibration
    {
        public VisualRegion Viewport { get; set; }
        public Dictionary<string, VisualMarker> Markers { get; set; }
        public Dictionary<string, VisualRegion> Counters { get; set; }
        public List<VisualRegion> Plots { get; set; }
        public List<VisualRegion> Slots { get; set; }
        public FieldArea Field { get; set; }

        public FarmCalibration()
        {
            Markers = new Dictionary<string, VisualMarker>();
            Counters = new Dictionary<string, VisualRegion>();
            Plots = new List<VisualRegion>();
            Slots = new List<VisualRegion>();
        }

        public void Normalize()
        {
            if (Markers == null) Markers = new Dictionary<string, VisualMarker>();
            if (Counters == null) Counters = new Dictionary<string, VisualRegion>();
            if (Plots == null) Plots = new List<VisualRegion>();
            if (Slots == null) Slots = new List<VisualRegion>();
        }

        public string[] Missing()
        {
            return Missing(false);
        }

        public string[] Missing(bool fieldOnly)
        {
            var missing = new List<string>();
            if (Viewport == null) missing.Add("Область Android");
            if (Field == null && Plots.Count == 0) missing.Add("Область грядок");
            if (!fieldOnly && Slots.Count == 0) missing.Add("Центры слотов");
            missing.AddRange(CalibrationCatalog.Items.Where(item =>
                item.Required && (!fieldOnly || CalibrationCatalog.FieldKeys.Contains(item.Key))
                && (item.Kind == MarkerKind.Marker && !Markers.ContainsKey(item.Key)
                    || item.Kind == MarkerKind.Counter && !Counters.ContainsKey(item.Key)))
                .Select(item => item.Name));
            return missing.ToArray();
        }

        internal Point[] PlotPoints()
        {
            return Field != null ? Field.Sweep(DesktopSurface.FrameWidth, DesktopSurface.FrameHeight) : Plots.Select(plot =>
                ScreenVision.Center(plot.ToRectangle(DesktopSurface.FrameWidth, DesktopSurface.FrameHeight))).ToArray();
        }
    }

    internal enum MarkerKind { Viewport, Field, Marker, Counter, Plot, Slot }

    internal sealed class MarkerItem
    {
        public readonly string Key, Name, Hint;
        public readonly MarkerKind Kind;
        public readonly bool Required;

        public MarkerItem(string key, string name, string hint, MarkerKind kind, bool required)
        {
            Key = key; Name = name; Hint = hint; Kind = kind; Required = required;
        }

        public override string ToString() { return Name; }
    }

    internal static class CalibrationCatalog
    {
        internal static readonly HashSet<string> FieldKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "farm", "anchor", "empty", "seeded", "ripe", "seed", "seedStock", "sickle"
        };

        private static MarkerItem Mark(string key, string name, string hint, bool required)
        {
            return new MarkerItem(key, name, hint, MarkerKind.Marker, required);
        }

        private static MarkerItem Counter(string key, string name, string hint, bool required = true)
        {
            return new MarkerItem(key, name, hint, MarkerKind.Counter, required);
        }

        public static readonly MarkerItem[] Items =
        {
            new MarkerItem("viewport", "Область Android", "Выдели игровое изображение целиком, без рамок и рекламы BlueStacks.", MarkerKind.Viewport, true),
            new MarkerItem("field", "Ферма · область грядок", "Совмести ориентир с полупрозрачной меткой. Для разметки поля отметь левый, верхний и нижний углы.", MarkerKind.Field, false),
            Mark("farm", "Ферма · элемент интерфейса", "Выдели постоянную кнопку, которая видна также при выборе семян и серпа. Обойди счётчики.", true),
            Mark("anchor", "Ферма · ориентир камеры", "Выдели неподвижную деталь рядом с грядками, видимую при выборе инструмента. Для продажи умести в кадре также киоск.", true),
            new MarkerItem("plot", "Ферма · центр грядки", "Выдели небольшую область в центре каждой грядки. Центры добавляются в порядке жеста.", MarkerKind.Plot, true),
            Mark("empty", "Грядка · пустая", "Выдели центральную часть пустой грядки.", true),
            Mark("seeded", "Грядка · засеянная", "Выдели участок того же размера на молодом посеве.", true),
            Mark("growing", "Грядка · растущая пшеница", "Выдели центральный участок зелёной пшеницы незадолго до созревания.", false),
            Mark("ripe", "Грядка · спелая", "Выдели участок того же размера на созревшей пшенице.", true),
            Mark("seed", "Инструмент · семена пшеницы", "Выдели именно перетаскиваемую иконку пшеницы в выборе семян.", true),
            Mark("seedTarget", "Инструмент · грядка под меню семян", "На том же кадре отметь центр грядки, на которой открыто меню семян.", false),
            Counter("seedStock", "Инструмент · количество семян", "Если хочешь предварительно проверять запас, обведи число на иконке пшеницы.", false),
            Mark("sickle", "Инструмент · серп", "Выдели иконку серпа, когда открыто меню спелой пшеницы.", true),
            Mark("sickleTarget", "Инструмент · грядка под меню серпа", "На том же кадре отметь центр грядки, на которой открыто меню серпа.", false),
            Mark("clearSelection", "Ферма · снять выделение с грядки", "Выдели свободный участок земли за пределами грядок. Нажатие должно только закрывать меню инструмента.", false),
            Mark("stall", "Ферма · придорожный киоск", "Выдели область киоска, по центру которой его можно открыть.", true),
            Mark("shop", "Киоск · заголовок", "Выдели постоянный элемент заголовка внутри своего киоска.", true),
            Mark("shopClose", "Киоск · закрыть", "Выдели красный крестик, которым закрывается собственный киоск.", false),
            new MarkerItem("slot", "Киоск · центр слота", "Выдели центр слота. Повтори для каждого проверяемого слота.", MarkerKind.Slot, true),
            Mark("free", "Киоск · свободный слот", "Выдели значок пустого слота в его центре.", true),
            Mark("sold", "Киоск · продано", "Выдели значок получения монет в центре проданного слота.", false),
            Mark("soldAlt", "Киоск · продано другому покупателю", "Сохрани альтернативный вид значка продажи по центру ячейки.", false),
            Mark("occupied", "Киоск · пшеница выставлена", "Выдели пшеницу по центру уже выставленной партии, если она видна.", false),
            Mark("listing", "Продажа · заголовок формы", "Выдели устойчивый элемент формы продажи.", true),
            Mark("listingClose", "Продажа · закрыть форму", "Выдели красный крестик, который возвращает из формы в киоск.", false),
            Counter("silo", "Продажа · заполненность силоса", "Выдели строку с числами через дробь над списком культур в форме продажи.", false),
            Mark("wheat", "Продажа · выбрать пшеницу", "Выдели доступную для выбора иконку пшеницы в форме продажи.", true),
            Mark("selected", "Продажа · пшеница выбрана", "Выдели постоянный элемент, который подтверждает выбор пшеницы.", true),
            Counter("stock", "Продажа · остаток пшеницы", "Обведи только число пшеницы в наличии. В области не должно быть других чисел."),
            Counter("amount", "Продажа · размер партии", "Обведи только число единиц в партии."),
            Counter("price", "Продажа · цена партии", "Обведи только числовую цену в монетах."),
            Mark("amountUp", "Продажа · прибавить единицу", "Выдели кнопку количества +.", true),
            Mark("amountDown", "Продажа · убрать единицу", "Выдели кнопку количества −.", true),
            Mark("priceUp", "Продажа · повысить цену", "Выдели кнопку цены + за монеты.", true),
            Mark("priceDown", "Продажа · снизить цену", "Выдели кнопку цены −.", true),
            Mark("priceMin", "Продажа · минимальная цена", "Выдели кнопку с двумя стрелками влево под ценой в монетах.", false),
            Mark("submit", "Продажа · выставить за монеты", "Выдели безопасную кнопку выставления. Отдельно сохрани форму без алмазов и ускорений.", true),
            Mark("coins", "Продажа · значок монеты", "Выдели значок монеты рядом с ценой, отдельно от кнопки выставления.", true),
            Mark("adFree", "Газета · бесплатное объявление", "Выдели заголовок объявления и строку под ним, когда объявление доступно бесплатно.", false),
            Mark("adFreePanel", "Газета · область условия объявления", "Выдели соседнюю область, где отображаются ожидание или стоимость, на кадре с бесплатной кнопкой.", false),
            Mark("adUnavailable", "Газета · объявление недоступно", "Выдели неактивную кнопку вместе с её фоном после публикации бесплатного объявления.", false),
            Mark("adOn", "Газета · бесплатное объявление выбрано", "Выдели тот же переключатель во включённом состоянии.", false),
            Mark("adOff", "Газета · объявление выключено", "Выдели переключатель объявлений в явном выключенном состоянии.", true),
            Mark("full", "Препятствие · заполнен силос", "Выдели неизменную часть сообщения о полном силосе.", false),
            Mark("noSeeds", "Препятствие · нет семян", "Выдели неизменную часть сообщения об отсутствии семян.", false),
            Mark("offline", "Препятствие · нет соединения", "Выдели сообщение о соединении, без кнопок платных действий.", false),
            Mark("reconnect", "Препятствие · переподключиться", "Выдели обычную кнопку переподключения без платных действий.", false),
            Mark("loading", "Препятствие · игра загружается", "Выдели логотип на экране загрузки.", false)
        };
    }

    internal static class ScreenVision
    {
        private const int Samples = 8;

        public static VisualMarker Make(Bitmap image, Rectangle selection)
        {
            var marker = new VisualMarker { Region = VisualRegion.FromRectangle(selection, image.Width, image.Height) };
            marker.Rgb = PixelSamples(image, selection);
            return marker;
        }

        public static double Distance(Bitmap image, VisualMarker marker, Rectangle location)
        {
            if (marker == null || marker.Rgb == null || marker.Rgb.Length != Samples * Samples
                || !new Rectangle(0, 0, image.Width, image.Height).Contains(location)) return Double.PositiveInfinity;
            int index = 0;
            int difference = 0;
            for (int y = 0; y < Samples; y++)
                for (int x = 0; x < Samples; x++)
                {
                    int expected = marker.Rgb[index++];
                    Color pixel = image.GetPixel(location.X + (2 * x + 1) * location.Width / (2 * Samples),
                        location.Y + (2 * y + 1) * location.Height / (2 * Samples));
                    difference += Math.Abs(pixel.R - ((expected >> 16) & 255));
                    difference += Math.Abs(pixel.G - ((expected >> 8) & 255));
                    difference += Math.Abs(pixel.B - (expected & 255));
                }
            return (double)difference / (Samples * Samples * 3);
        }

        public static bool Matches(Bitmap image, VisualMarker marker, int limit)
        {
            return marker != null && Distance(image, marker, Base(image, marker)) <= limit;
        }

        public static Rectangle Base(Bitmap image, VisualMarker marker) { return marker.Region.ToRectangle(image.Width, image.Height); }

        public static Rectangle At(Bitmap image, VisualMarker marker, Point point)
        {
            Rectangle area = Base(image, marker);
            return new Rectangle(point.X - area.Width / 2, point.Y - area.Height / 2, area.Width, area.Height);
        }

        public static Point Center(Rectangle area) { return new Point(area.Left + area.Width / 2, area.Top + area.Height / 2); }

        public static Rectangle? Locate(Bitmap image, VisualMarker marker, Rectangle expected, int radius, int limit)
        {
            if (marker == null || marker.Rgb == null || marker.Rgb.Length != Samples * Samples) return null;
            double best = limit + 0.01;
            Rectangle? match = null;
            int step = radius >= 45 ? 4 : radius <= 6 ? 1 : 2;
            double original = Distance(image, marker, expected);
            if (original == 0) return expected;
            if (original <= limit) { best = original; match = expected; }
            PixelSearch pixels = radius >= 45 ? new PixelSearch(image, marker, expected.Size) : null;
            int firstY = Math.Max(-radius, -expected.Top);
            int lastY = Math.Min(radius, image.Height - expected.Bottom);
            int firstX = Math.Max(-radius, -expected.Left);
            int lastX = Math.Min(radius, image.Width - expected.Right);
            for (int dy = firstY; dy <= lastY; dy += step)
                for (int dx = firstX; dx <= lastX; dx += step)
                {
                    var candidate = new Rectangle(expected.X + dx, expected.Y + dy, expected.Width, expected.Height);
                    double distance = pixels == null ? Distance(image, marker, candidate) : pixels.Distance(candidate, best);
                    if (distance < best)
                    {
                        best = distance;
                        match = candidate;
                    }
                }
            if (match.HasValue)
            {
                Rectangle coarse = match.Value;
                for (int dy = 1 - step; dy < step; dy++)
                    for (int dx = 1 - step; dx < step; dx++)
                    {
                        Rectangle candidate = coarse;
                        candidate.Offset(dx, dy);
                        double distance = Distance(image, marker, candidate);
                        if (distance < best)
                        {
                            best = distance;
                            match = candidate;
                        }
                    }
            }
            return match;
        }

        private sealed class PixelSearch
        {
            private readonly byte[] bgr;
            private readonly int stride;
            private readonly int[] offsets = new int[Samples * Samples];
            private readonly int[] sample;

            public PixelSearch(Bitmap image, VisualMarker marker, Size size)
            {
                stride = image.Width * 3;
                bgr = new byte[stride * image.Height];
                BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < image.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bgr, y * stride, stride);
                }
                finally { image.UnlockBits(data); }
                sample = marker.Rgb;
                for (int y = 0; y < Samples; y++)
                    for (int x = 0; x < Samples; x++)
                        offsets[y * Samples + x] = (2 * y + 1) * size.Height / (2 * Samples) * stride
                            + (2 * x + 1) * size.Width / (2 * Samples) * 3;
            }

            public double Distance(Rectangle candidate, double best)
            {
                int sum = 0;
                int limit = (int)(best * Samples * Samples * 3);
                int origin = candidate.Y * stride + candidate.X * 3;
                for (int i = 0; i < sample.Length; i++)
                {
                    int position = origin + offsets[i];
                    int color = sample[i];
                    sum += Math.Abs(bgr[position] - (color & 255)) +
                        Math.Abs(bgr[position + 1] - ((color >> 8) & 255)) +
                        Math.Abs(bgr[position + 2] - ((color >> 16) & 255));
                    if (sum > limit) return Double.PositiveInfinity;
                }
                return (double)sum / (Samples * Samples * 3);
            }
        }

        private static int[] PixelSamples(Bitmap image, Rectangle location)
        {
            var data = new int[Samples * Samples];
            int index = 0;
            for (int y = 0; y < Samples; y++)
                for (int x = 0; x < Samples; x++)
                    data[index++] = image.GetPixel(location.X + (2 * x + 1) * location.Width / (2 * Samples),
                        location.Y + (2 * y + 1) * location.Height / (2 * Samples)).ToArgb() & 0xffffff;
            return data;
        }
    }
}
