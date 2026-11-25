using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FarmDesk
{
    internal static class StandLayout
    {
        public const int Width = 960;
        public const int Height = 540;
        public const int MaxPlots = 8;
        public const int MaxSlots = 4;
        public const int MaxLot = 10;
        public const int MaxPrice = 60;

        public static readonly Point ScreenProbe = new Point(32, 32);
        public static readonly Point ToolPoint = new Point(178, 395);
        public static readonly Point StallPoint = new Point(836, 444);
        public static readonly Point BackPoint = new Point(106, 449);
        public static readonly Point WheatPoint = new Point(480, 190);
        public static readonly Point AmountMinus = new Point(244, 309);
        public static readonly Point AmountPlus = new Point(395, 309);
        public static readonly Point PriceMinus = new Point(565, 309);
        public static readonly Point PricePlus = new Point(716, 309);
        public static readonly Point AdPoint = new Point(656, 375);
        public static readonly Point ConfirmPoint = new Point(795, 449);
        public static readonly Point StockCounter = new Point(686, 42);
        public static readonly Point CoinCounter = new Point(829, 42);
        public static readonly Point AmountCounter = new Point(295, 298);
        public static readonly Point PriceCounter = new Point(616, 298);
        public static readonly Point StatusProbe = new Point(41, 507);

        public static readonly Color Loading = Color.FromArgb(126, 138, 150);
        public static readonly Color Field = Color.FromArgb(36, 163, 111);
        public static readonly Color SeedPanel = Color.FromArgb(38, 154, 255);
        public static readonly Color SeedReady = Color.FromArgb(34, 197, 198);
        public static readonly Color HarvestPanel = Color.FromArgb(238, 157, 60);
        public static readonly Color HarvestReady = Color.FromArgb(210, 103, 62);
        public static readonly Color Shop = Color.FromArgb(157, 112, 213);
        public static readonly Color Listing = Color.FromArgb(99, 135, 213);
        public static readonly Color Disconnected = Color.FromArgb(203, 76, 85);

        public static readonly Color EmptyPlot = Color.FromArgb(125, 80, 96);
        public static readonly Color SeededPlot = Color.FromArgb(50, 141, 113);
        public static readonly Color RipePlot = Color.FromArgb(222, 172, 69);
        public static readonly Color FreeSlot = Color.FromArgb(31, 126, 97);
        public static readonly Color OccupiedSlot = Color.FromArgb(63, 89, 132);
        public static readonly Color SoldSlot = Color.FromArgb(197, 139, 51);
        public static readonly Color WheatAvailable = Color.FromArgb(209, 179, 111);
        public static readonly Color WheatSelected = Color.FromArgb(52, 185, 130);
        public static readonly Color AdFree = Color.FromArgb(41, 150, 96);
        public static readonly Color AdBlocked = Color.FromArgb(171, 66, 77);
        public static readonly Color AdSelected = Color.FromArgb(84, 208, 148);
        public static readonly Color ConfirmEnabled = Color.FromArgb(34, 154, 112);
        public static readonly Color ConfirmDisabled = Color.FromArgb(80, 91, 104);
        public static readonly Color StatusNormal = Color.FromArgb(64, 156, 140);
        public static readonly Color StatusSiloFull = Color.FromArgb(232, 108, 77);
        public static readonly Color StatusNoSeeds = Color.FromArgb(218, 76, 109);
        public static readonly Color StatusNoSlots = Color.FromArgb(202, 132, 83);

        private static readonly Color LitSegment = Color.FromArgb(230, 249, 236);
        private static readonly Color DimSegment = Color.FromArgb(52, 65, 72);
        private static readonly Rectangle[] Segments = {
            new Rectangle(3, 0, 9, 2), new Rectangle(12, 2, 2, 8),
            new Rectangle(12, 12, 2, 8), new Rectangle(3, 20, 9, 2),
            new Rectangle(1, 12, 2, 8), new Rectangle(1, 2, 2, 8),
            new Rectangle(3, 10, 9, 2)
        };
        private static readonly int[] Digits = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };

        public static Point PlotPoint(int index)
        {
            return new Point(101 + 109 * index, 268);
        }

        public static Point SlotPoint(int index)
        {
            return new Point(201 + 186 * index, 254);
        }

        public static int ReadCounter(Bitmap frame, Point origin)
        {
            if (frame == null || origin.X < 0 || origin.Y < 0 ||
                origin.X + 45 >= frame.Width || origin.Y + 21 >= frame.Height)
                return -1;

            int value = 0;
            for (int digit = 0; digit < 3; digit++)
            {
                int pattern = 0;
                for (int segment = 0; segment < Segments.Length; segment++)
                {
                    Rectangle rect = Segments[segment];
                    int pixel = frame.GetPixel(origin.X + 16 * digit + rect.X + rect.Width / 2,
                        origin.Y + rect.Y + rect.Height / 2).ToArgb();
                    if (pixel == LitSegment.ToArgb()) pattern |= 1 << segment;
                    else if (pixel != DimSegment.ToArgb()) return -1;
                }
                int number = Array.IndexOf(Digits, pattern);
                if (number < 0) return -1;
                value = value * 10 + number;
            }
            return value;
        }

        internal static void DrawCounter(Graphics graphics, Point origin, int value)
        {
            value = Math.Max(0, Math.Min(999, value));
            using (var lit = new SolidBrush(LitSegment))
            using (var dim = new SolidBrush(DimSegment))
            {
                for (int digit = 2; digit >= 0; digit--)
                {
                    int pattern = Digits[value % 10];
                    value /= 10;
                    for (int segment = 0; segment < Segments.Length; segment++)
                    {
                        Rectangle rect = Segments[segment];
                        rect.Offset(origin.X + 16 * digit, origin.Y);
                        graphics.FillRectangle((pattern & (1 << segment)) != 0 ? lit : dim, rect);
                    }
                }
            }
        }
    }

    internal sealed class CycleStand : IDisposable
    {
        private enum View { Field, SeedPanel, SeedReady, HarvestPanel, HarvestReady, Shop, Listing }
        private enum PlotState { Empty, Seeded, Ripe }

        private sealed class Plot
        {
            public PlotState State;
            public double RipeAt;
        }

        private sealed class Slot
        {
            public int Wheat;
            public int Price;
            public double SellAt;
            public bool Sold;
            public bool Advertised;
        }

        private static readonly Color Background = Color.FromArgb(17, 23, 29);
        private static readonly Color Card = Color.FromArgb(27, 36, 44);
        private static readonly Color Tile = Color.FromArgb(35, 46, 54);
        private static readonly Color Text = Color.FromArgb(241, 245, 245);
        private static readonly Color Quiet = Color.FromArgb(169, 184, 189);
        private static readonly Color Amber = Color.FromArgb(229, 187, 109);

        private readonly object gate = new object();
        private readonly int siloCapacity;
        private readonly int secondsToSell;
        private readonly double loadingSeconds;
        private readonly Func<double> now;
        private readonly Plot[] plots;
        private readonly Slot[] slots;
        private readonly Font heading = new Font("Segoe UI", 21, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font label = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font small = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font tiny = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);

        private int stock;
        private int coins;
        private View view;
        private int selectedSlot;
        private int amount;
        private int price;
        private bool wheatSelected;
        private bool advertisementSelected;
        private bool online = true;
        private bool disposed;
        private double adReadyAt;
        private double loadsAt;
        private string message = "Стенд готов к проверке.";
        private Color status = StandLayout.StatusNormal;

        public CycleStand() : this(12, 80, 6, 3, 4, 14, true, null) { }

        public CycleStand(int startingStock, int siloCapacity, int plotCount, int stallSlots,
            int secondsToRipe, int secondsToSell = 14, bool seedMarket = true, Func<double> seconds = null)
        {
            if (siloCapacity < 1 || siloCapacity > 999) throw new ArgumentOutOfRangeException("siloCapacity");
            if (startingStock < 0 || startingStock > siloCapacity) throw new ArgumentOutOfRangeException("startingStock");
            if (plotCount < 1 || plotCount > StandLayout.MaxPlots) throw new ArgumentOutOfRangeException("plotCount");
            if (stallSlots < 1 || stallSlots > StandLayout.MaxSlots) throw new ArgumentOutOfRangeException("stallSlots");
            if (secondsToRipe < 1 || secondsToSell < 1) throw new ArgumentOutOfRangeException("secondsToRipe");

            var elapsed = Stopwatch.StartNew();
            now = seconds ?? (() => elapsed.Elapsed.TotalSeconds);
            loadingSeconds = 0.55;
            loadsAt = now() + loadingSeconds;
            adReadyAt = loadsAt;
            stock = startingStock;
            this.siloCapacity = siloCapacity;
            this.secondsToSell = secondsToSell;
            SecondsToRipe = secondsToRipe;
            plots = new Plot[plotCount];
            for (int i = 0; i < plots.Length; i++) plots[i] = new Plot();
            slots = new Slot[stallSlots];
            if (seedMarket && stallSlots >= 2) slots[0] = new Slot { Wheat = 3, Price = 6, Sold = true };
            if (seedMarket && stallSlots >= 3)
                slots[stallSlots - 1] = new Slot { Wheat = 2, Price = 4, SellAt = loadsAt + secondsToSell + 3 };
        }

        public int SecondsToRipe { get; private set; }

        public void SetConnection(bool connected)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (online == connected) return;
                online = connected;
                if (connected) RestartViewCore();
            }
        }

        public void RestartView()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                RestartViewCore();
            }
        }

        private void RestartViewCore()
        {
            view = View.Field;
            loadsAt = now() + loadingSeconds;
            SetMessage("Экран открывается заново. Состояние участка сохранено.");
        }

        public Bitmap Capture()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                double time = now();
                Advance(time);
                var bitmap = new Bitmap(StandLayout.Width, StandLayout.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        graphics.Clear(Background);
                        DrawHeader(graphics, time);
                        if (!online || time < loadsAt) DrawUnavailable(graphics, time);
                        else if (view == View.Shop) DrawShop(graphics);
                        else if (view == View.Listing) DrawListing(graphics, time);
                        else DrawField(graphics, time);
                        DrawFooter(graphics, time);
                    }
                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }
        }

        public void Tap(Point point)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                double time = now();
                if (!online || time < loadsAt) return;
                Advance(time);
                if (view == View.Shop) TapShop(point);
                else if (view == View.Listing) TapListing(point, time);
                else TapField(point);
            }
        }

        public void Drag(Point from, Point to)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                double time = now();
                if (!online || time < loadsAt) return;
                Advance(time);
                bool sow = view == View.SeedReady;
                if (!sow && view != View.HarvestReady) return;

                int affected = 0;
                var touched = new bool[plots.Length];
                for (int i = 0; i < plots.Length; i++)
                    touched[i] = DistanceSquared(StandLayout.PlotPoint(i), from, to) <= 38 * 38;

                for (int step = 0; step < plots.Length; step++)
                {
                    int i = from.X <= to.X ? step : plots.Length - 1 - step;
                    if (!touched[i]) continue;
                    Plot plot = plots[i];
                    if (sow && plot.State == PlotState.Empty && stock > 0)
                    {
                        plot.State = PlotState.Seeded;
                        plot.RipeAt = time + SecondsToRipe;
                        stock--;
                        affected++;
                    }
                    else if (!sow && plot.State == PlotState.Ripe && stock + 2 <= siloCapacity)
                    {
                        plot.State = PlotState.Empty;
                        stock += 2;
                        affected++;
                    }
                }

                view = View.Field;
                if (affected > 0)
                    SetMessage((sow ? "Посев, грядок: " : "Сбор, грядок: ") + affected + ".");
                else if (sow && stock == 0)
                    SetMessage("Запас семян исчерпан.", StandLayout.StatusNoSeeds);
                else if (!sow && stock + 2 > siloCapacity)
                    SetMessage("Для урожая не хватает места в силосе.", StandLayout.StatusSiloFull);
                else
                    SetMessage("Жест прошёл вне подходящих грядок.");
            }
        }

        private void TapField(Point point)
        {
            if (Near(point, StandLayout.StallPoint, 76, 26))
            {
                view = View.Shop;
                SetMessage(HasFreeSlot() ? "Витрина открыта." : "Все места витрины заняты.",
                    HasFreeSlot() ? StandLayout.StatusNormal : StandLayout.StatusNoSlots);
                return;
            }
            if (Near(point, StandLayout.ToolPoint, 93, 28))
            {
                if (view == View.SeedPanel)
                {
                    if (stock == 0) SetMessage("Для посева нужны семена.", StandLayout.StatusNoSeeds);
                    else { view = View.SeedReady; SetMessage("Семена выбраны. Проведи по пустым грядкам."); }
                }
                else if (view == View.HarvestPanel)
                {
                    if (stock + 2 > siloCapacity) SetMessage("Для урожая не хватает места в силосе.", StandLayout.StatusSiloFull);
                    else { view = View.HarvestReady; SetMessage("Инструмент выбран. Проведи по созревшим грядкам."); }
                }
                return;
            }
            for (int i = 0; i < plots.Length; i++)
            {
                if (!Near(point, StandLayout.PlotPoint(i), 45, 46)) continue;
                switch (plots[i].State)
                {
                    case PlotState.Empty:
                        view = View.SeedPanel;
                        SetMessage(stock > 0 ? "Доступна пшеница для посева." : "Запас семян исчерпан.",
                            stock > 0 ? StandLayout.StatusNormal : StandLayout.StatusNoSeeds);
                        break;
                    case PlotState.Seeded:
                        view = View.Field;
                        SetMessage("Посев ещё созревает.");
                        break;
                    case PlotState.Ripe:
                        view = View.HarvestPanel;
                        SetMessage(stock + 2 <= siloCapacity ? "Можно собирать урожай." : "Для урожая не хватает места в силосе.",
                            stock + 2 <= siloCapacity ? StandLayout.StatusNormal : StandLayout.StatusSiloFull);
                        break;
                }
                return;
            }
        }

        private void TapShop(Point point)
        {
            if (Near(point, StandLayout.BackPoint, 71, 24))
            {
                view = View.Field;
                SetMessage("Участок открыт.");
                return;
            }
            for (int i = 0; i < slots.Length; i++)
            {
                if (!Near(point, StandLayout.SlotPoint(i), 74, 69)) continue;
                Slot slot = slots[i];
                if (slot == null)
                {
                    selectedSlot = i;
                    wheatSelected = false;
                    advertisementSelected = false;
                    amount = 1;
                    price = 1;
                    view = View.Listing;
                    SetMessage("Свободное место выбрано.");
                }
                else if (slot.Sold)
                {
                    coins = Math.Min(999, coins + slot.Price);
                    slots[i] = null;
                    SetMessage("Монеты за проданную партию получены.");
                }
                else
                    SetMessage("Партия остаётся на витрине.");
                return;
            }
        }

        private void TapListing(Point point, double time)
        {
            if (Near(point, StandLayout.BackPoint, 71, 24))
            {
                view = View.Shop;
                SetMessage("Витрина открыта.");
            }
            else if (Near(point, StandLayout.WheatPoint, 115, 35))
            {
                if (stock > 0) { wheatSelected = true; SetMessage("Пшеница выбрана для партии."); }
                else SetMessage("Для партии нужна пшеница.", StandLayout.StatusNoSeeds);
            }
            else if (wheatSelected && Near(point, StandLayout.AmountMinus, 27, 23))
                amount = Math.Max(1, amount - 1);
            else if (wheatSelected && Near(point, StandLayout.AmountPlus, 27, 23))
                amount = Math.Max(1, Math.Min(Math.Min(StandLayout.MaxLot, stock), amount + 1));
            else if (wheatSelected && Near(point, StandLayout.PriceMinus, 27, 23))
                price = Math.Max(1, price - 1);
            else if (wheatSelected && Near(point, StandLayout.PricePlus, 27, 23))
                price = Math.Min(StandLayout.MaxPrice, price + 1);
            else if (Near(point, StandLayout.AdPoint, 75, 25) && time >= adReadyAt)
                advertisementSelected = !advertisementSelected;
            else if (Near(point, StandLayout.ConfirmPoint, 76, 25) && CanList())
            {
                bool withAd = advertisementSelected && time >= adReadyAt;
                slots[selectedSlot] = new Slot { Wheat = amount, Price = price, SellAt = time + secondsToSell, Advertised = withAd };
                stock -= amount;
                if (withAd) adReadyAt = time + 30;
                view = View.Shop;
                SetMessage(withAd ? "Партия выставлена с бесплатным объявлением." : "Партия выставлена.");
            }
        }

        private bool CanList()
        {
            return wheatSelected && amount <= stock && slots[selectedSlot] == null;
        }

        private bool HasFreeSlot()
        {
            return Array.Exists(slots, slot => slot == null);
        }

        private void Advance(double time)
        {
            foreach (Plot plot in plots)
                if (plot.State == PlotState.Seeded && time >= plot.RipeAt) plot.State = PlotState.Ripe;
            foreach (Slot slot in slots)
                if (slot != null && !slot.Sold && time >= slot.SellAt) slot.Sold = true;
        }

        private void DrawHeader(Graphics graphics, double time)
        {
            RoundFill(graphics, Card, new Rectangle(16, 14, 928, 72), 15);
            Color signal;
            string mode;
            if (!online) { signal = StandLayout.Disconnected; mode = "Соединение прервано"; }
            else if (time < loadsAt) { signal = StandLayout.Loading; mode = "Загрузка"; }
            else
            {
                switch (view)
                {
                    case View.SeedPanel: signal = StandLayout.SeedPanel; mode = "Выбор семян"; break;
                    case View.SeedReady: signal = StandLayout.SeedReady; mode = "Посев"; break;
                    case View.HarvestPanel: signal = StandLayout.HarvestPanel; mode = "Выбор инструмента"; break;
                    case View.HarvestReady: signal = StandLayout.HarvestReady; mode = "Сбор"; break;
                    case View.Shop: signal = StandLayout.Shop; mode = "Витрина"; break;
                    case View.Listing: signal = StandLayout.Listing; mode = "Размещение партии"; break;
                    default: signal = StandLayout.Field; mode = "Участок"; break;
                }
            }
            Probe(graphics, StandLayout.ScreenProbe, signal, 9);
            Write(graphics, "Стенд агроопераций", heading, Text, 58, 21);
            Write(graphics, mode, small, Quiet, 60, 56);
            RoundFill(graphics, Tile, new Rectangle(664, 22, 130, 56), 10);
            RoundFill(graphics, Tile, new Rectangle(808, 22, 121, 56), 10);
            Write(graphics, "Пшеница", tiny, Quiet, 685, 25);
            Write(graphics, "Монеты", tiny, Quiet, 828, 25);
            StandLayout.DrawCounter(graphics, StandLayout.StockCounter, stock);
            StandLayout.DrawCounter(graphics, StandLayout.CoinCounter, coins);
        }

        private void DrawField(Graphics graphics, double time)
        {
            RoundFill(graphics, Card, new Rectangle(16, 100, 928, 242), 16);
            Write(graphics, "Участок", heading, Text, 32, 119);
            Write(graphics, "Грядок " + plots.Length + "  ·  Ёмкость силоса " + siloCapacity, small, Quiet, 33, 151);
            for (int i = 0; i < plots.Length; i++)
            {
                Point point = StandLayout.PlotPoint(i);
                var tile = new Rectangle(point.X - 47, point.Y - 59, 94, 112);
                RoundFill(graphics, Tile, tile, 12);
                Plot plot = plots[i];
                bool ripe = plot.State == PlotState.Ripe;
                Color color = ripe ? StandLayout.RipePlot : plot.State == PlotState.Seeded ? StandLayout.SeededPlot : StandLayout.EmptyPlot;
                string stateText = ripe ? "Созрела" : plot.State == PlotState.Seeded ? "Растёт" : "Пустая";
                Write(graphics, (i + 1).ToString("00"), tiny, Quiet, tile.X + 9, tile.Y + 7);
                for (int column = -2; column <= 2; column++)
                {
                    int height = ripe ? 30 + Math.Abs(column) * 3 : plot.State == PlotState.Seeded ? 18 : 8;
                    RoundFill(graphics, color, new Rectangle(point.X - 3 + column * 10, point.Y + 16 - height, 6, height), 3);
                }
                Probe(graphics, point, color, 6);
                Center(graphics, stateText, small, Text, new Rectangle(tile.X, point.Y + 20, tile.Width, 26));
            }

            RoundFill(graphics, Card, new Rectangle(16, 354, 928, 128), 16);
            if (view == View.SeedPanel || view == View.SeedReady || view == View.HarvestPanel || view == View.HarvestReady)
            {
                bool sow = view == View.SeedPanel || view == View.SeedReady;
                bool ready = view == View.SeedReady || view == View.HarvestReady;
                bool available = sow ? stock > 0 : stock + 2 <= siloCapacity;
                Button(graphics, StandLayout.ToolPoint, 190, 48, sow ? "Выбрать пшеницу" : "Инструмент сбора",
                    available ? sow ? StandLayout.SeedReady : Amber : StandLayout.ConfirmDisabled);
                Write(graphics, ready ? "Проведи инструментом через грядки." : "Выбери инструмент для жеста.",
                    small, Quiet, 82, 432);
            }
            else
            {
                Write(graphics, "Посев и сбор", label, Text, 36, 370);
                double soonest = double.MaxValue;
                foreach (Plot plot in plots)
                    if (plot.State == PlotState.Seeded) soonest = Math.Min(soonest, plot.RipeAt - time);
                Write(graphics, soonest == double.MaxValue ? "Выбери грядку." : "До первого урожая " + Math.Ceiling(Math.Max(0, soonest)) + " с.",
                    small, Quiet, 37, 398);
            }
            Button(graphics, StandLayout.StallPoint, 164, 45, "Открыть витрину", StandLayout.Field);
        }

        private void DrawShop(Graphics graphics)
        {
            RoundFill(graphics, Card, new Rectangle(16, 100, 928, 382), 16);
            Write(graphics, "Витрина", heading, Text, 33, 121);
            Write(graphics, "Нажми на проданную партию, чтобы забрать монеты.", small, Quiet, 34, 154);
            for (int i = 0; i < slots.Length; i++)
            {
                Point point = StandLayout.SlotPoint(i);
                var box = new Rectangle(point.X - 75, point.Y - 51, 150, 139);
                RoundFill(graphics, Tile, box, 12);
                Slot slot = slots[i];
                Color color = slot == null ? StandLayout.FreeSlot : slot.Sold ? StandLayout.SoldSlot : StandLayout.OccupiedSlot;
                string title = slot == null ? "Свободно" : slot.Sold ? "Продано" : "На продаже";
                Write(graphics, "Место " + (i + 1), tiny, Quiet, box.X + 12, box.Y + 9);
                Probe(graphics, point, color, 13);
                Center(graphics, title, label, Text, new Rectangle(box.X, point.Y + 17, box.Width, 28));
                if (slot != null)
                {
                    Center(graphics, slot.Wheat + " шт.  ·  " + slot.Price + " монет", small, Quiet,
                        new Rectangle(box.X, point.Y + 48, box.Width, 21));
                    if (slot.Advertised)
                        Center(graphics, "С объявлением", tiny, Quiet, new Rectangle(box.X, point.Y + 68, box.Width, 18));
                }
            }
            Button(graphics, StandLayout.BackPoint, 145, 45, "На участок", Tile);
        }

        private void DrawListing(Graphics graphics, double time)
        {
            RoundFill(graphics, Card, new Rectangle(16, 100, 928, 382), 16);
            Write(graphics, "Партия для места " + (selectedSlot + 1), heading, Text, 33, 121);
            RoundFill(graphics, Tile, new Rectangle(365, 160, 230, 60), 12);
            Probe(graphics, StandLayout.WheatPoint, wheatSelected ? StandLayout.WheatSelected : StandLayout.WheatAvailable, 9);
            Write(graphics, "Пшеница", label, Text, 400, 180);
            Write(graphics, wheatSelected ? "Выбрана" : "Выбрать", tiny, Quiet, 503, 183);
            DrawStepper(graphics, "Размер партии", StandLayout.AmountMinus, StandLayout.AmountPlus, StandLayout.AmountCounter, amount);
            DrawStepper(graphics, "Цена за партию", StandLayout.PriceMinus, StandLayout.PricePlus, StandLayout.PriceCounter, price);

            bool adAvailable = time >= adReadyAt;
            Color adColor = !adAvailable ? StandLayout.AdBlocked : advertisementSelected ? StandLayout.AdSelected : StandLayout.AdFree;
            RoundFill(graphics, Tile, new Rectangle(479, 353, 303, 46), 10);
            Write(graphics, "Объявление", small, Text, 491, 366);
            Probe(graphics, StandLayout.AdPoint, adColor, 9);
            Write(graphics, !adAvailable ? "Недоступно" : advertisementSelected ? "Выбрано" : "Бесплатно", tiny, Quiet, 675, 366);

            Button(graphics, StandLayout.BackPoint, 145, 45, "К витрине", Tile);
            Color actionColor = CanList() ? StandLayout.ConfirmEnabled : StandLayout.ConfirmDisabled;
            Button(graphics, StandLayout.ConfirmPoint, 165, 45, "Выставить", actionColor);
            Probe(graphics, StandLayout.ConfirmPoint, actionColor, 1);
        }

        private void DrawStepper(Graphics graphics, string title, Point smaller, Point larger, Point counter, int value)
        {
            RoundFill(graphics, Tile, new Rectangle(smaller.X - 30, 248, larger.X - smaller.X + 60, 89), 11);
            Center(graphics, title, small, Quiet, new Rectangle(smaller.X - 30, 250, larger.X - smaller.X + 60, 24));
            Button(graphics, smaller, 46, 37, "−", Background);
            Button(graphics, larger, 46, 37, "+", Background);
            StandLayout.DrawCounter(graphics, counter, value);
        }

        private void DrawUnavailable(Graphics graphics, double time)
        {
            RoundFill(graphics, Card, new Rectangle(16, 100, 928, 382), 16);
            if (!online)
            {
                Center(graphics, "Соединение прервано", heading, Text, new Rectangle(20, 232, 920, 38));
                Center(graphics, "Проверочный экран остановки сценария.", small, Quiet, new Rectangle(20, 272, 920, 28));
                return;
            }
            Center(graphics, "Открывается участок", heading, Text, new Rectangle(20, 232, 920, 38));
            RoundFill(graphics, Tile, new Rectangle(320, 280, 320, 8), 4);
            int fill = (int)(320 * Math.Max(0, Math.Min(1, 1 - (loadsAt - time) / loadingSeconds)));
            if (fill > 0) RoundFill(graphics, StandLayout.Field, new Rectangle(320, 280, fill, 8), 4);
        }

        private void DrawFooter(Graphics graphics, double time)
        {
            RoundFill(graphics, Card, new Rectangle(16, 491, 928, 33), 9);
            Color signal = !online ? StandLayout.Disconnected : time < loadsAt ? StandLayout.Loading : status;
            Probe(graphics, StandLayout.StatusProbe, signal, 5);
            Write(graphics, !online ? "Эмуляция сбоя сети." : message, small, Quiet, 57, 497);
        }

        private void Button(Graphics graphics, Point point, int width, int height, string name, Color color)
        {
            var bounds = new Rectangle(point.X - width / 2, point.Y - height / 2, width, height);
            RoundFill(graphics, color, bounds, 10);
            int brightness = color.R * 3 + color.G * 6 + color.B;
            Center(graphics, name, label, brightness > 1500 ? Background : Text, bounds);
        }

        private static void RoundFill(Graphics graphics, Color color, Rectangle rect, int radius)
        {
            using (var brush = new SolidBrush(color))
            using (var shape = new GraphicsPath())
            {
                int diameter = Math.Min(2 * radius, Math.Min(rect.Width, rect.Height));
                shape.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                shape.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                shape.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                shape.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
                shape.CloseFigure();
                graphics.FillPath(brush, shape);
            }
        }

        private static void Probe(Graphics graphics, Point point, Color color, int radius)
        {
            using (var brush = new SolidBrush(color))
                graphics.FillRectangle(brush, point.X - radius, point.Y - radius, 2 * radius + 1, 2 * radius + 1);
        }

        private static void Write(Graphics graphics, string value, Font font, Color color, int x, int y)
        {
            using (var brush = new SolidBrush(color)) graphics.DrawString(value, font, brush, x, y);
        }

        private static void Center(Graphics graphics, string value, Font font, Color color, Rectangle rect)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(value, font, brush, rect, format);
        }

        private static bool Near(Point actual, Point target, int x, int y)
        {
            return Math.Abs(actual.X - target.X) <= x && Math.Abs(actual.Y - target.Y) <= y;
        }

        private static double DistanceSquared(Point point, Point from, Point to)
        {
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;
            double length = (double)dx * dx + (double)dy * dy;
            double along = length == 0 ? 0 : ((point.X - from.X) * (double)dx + (point.Y - from.Y) * (double)dy) / length;
            along = Math.Min(1, Math.Max(0, along));
            double x = from.X + dx * along - point.X;
            double y = from.Y + dy * along - point.Y;
            return x * x + y * y;
        }

        private void SetMessage(string value)
        {
            SetMessage(value, StandLayout.StatusNormal);
        }

        private void SetMessage(string value, Color color)
        {
            message = value;
            status = color;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("CycleStand");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                heading.Dispose();
                label.Dispose();
                small.Dispose();
                tiny.Dispose();
            }
        }
    }
}
