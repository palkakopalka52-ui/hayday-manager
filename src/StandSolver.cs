using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FarmDesk
{
    internal sealed class StandSolver
    {
        private readonly CycleStand stand;
        private readonly FarmOptions options;
        private readonly CancellationToken stop;
        private readonly Action<string> report;
        private readonly CycleResult result = new CycleResult();

        public StandSolver(CycleStand stand, FarmOptions options, CancellationToken stop, Action<string> report)
        {
            this.stand = stand;
            this.options = options;
            this.stop = stop;
            this.report = report;
        }

        public async Task<CycleResult> RunAsync()
        {
            var time = Stopwatch.StartNew();
            Status("Загрузка стенда");
            await WaitForField();
            List<int> all = PlotIndices(StandLayout.EmptyPlot);
            all.AddRange(PlotIndices(StandLayout.SeededPlot));
            all.AddRange(PlotIndices(StandLayout.RipePlot));
            if (all.Count == 0) throw new CycleStop("На экране нет распознанных грядок.");
            options.Validate(all.Count);

            result.Sown = Sow(PlotIndices(StandLayout.EmptyPlot), "Посев пшеницы");
            await WaitForWheat(all.Count);
            Harvest();
            result.Replanted = Sow(PlotIndices(StandLayout.EmptyPlot), "Повторный посев");
            SellSurplus();
            result.Duration = time.Elapsed;
            result.FinalStatus = result.Listed > 0 ? "Цикл завершён" : "Урожай засеян повторно";
            return result;
        }

        private async Task WaitForField()
        {
            for (int check = 0; check < 28; check++)
            {
                Color screen = Screen();
                if (Same(screen, StandLayout.Field)) return;
                Require(Same(screen, StandLayout.Loading), "Неизвестный экран при ожидании фермы.");
                await Task.Delay(180, stop);
            }
            throw new CycleStop("Экран фермы не открылся.");
        }

        private async Task WaitForWheat(int count)
        {
            Status("Созревает · редкая проверка");
            int deadlineSeconds = Math.Max(4, stand.SecondsToRipe + 4);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < deadlineSeconds)
            {
                VerifyScreen(StandLayout.Field, "Ожидание созревания прервано: экран изменился.");
                if (PlotIndices(StandLayout.RipePlot).Count == count) return;
                await Task.Delay(550, stop);
            }
            throw new CycleStop("Грядки не созрели в ожидаемый интервал.");
        }

        private int Sow(List<int> plots, string stage)
        {
            if (plots.Count == 0) return 0;
            Status(stage);
            int before = ReadCounter(StandLayout.StockCounter);
            Require(before >= plots.Count, "Недостаточно семян для выбранных грядок.");
            Tap(StandLayout.PlotPoint(plots[0]));
            VerifyScreen(StandLayout.SeedPanel, "Окно выбора семян не открылось.");
            Tap(StandLayout.ToolPoint);
            VerifyScreen(StandLayout.SeedReady, "Пшеница недоступна для посева.");
            Drag(StandLayout.PlotPoint(plots[0]), StandLayout.PlotPoint(plots[plots.Count - 1]));
            VerifyScreen(StandLayout.Field, "После посева экран фермы не распознан.");
            int applied = before - ReadCounter(StandLayout.StockCounter);
            Require(applied == plots.Count && PlotIndices(StandLayout.EmptyPlot).Count == 0,
                "Посев затронул неполный ряд. Проверь участок.");
            return applied;
        }

        private void Harvest()
        {
            Status("Сбор урожая");
            var ripe = PlotIndices(StandLayout.RipePlot);
            Require(ripe.Count > 0, "Созревших грядок не найдено.");
            int before = ReadCounter(StandLayout.StockCounter);
            Tap(StandLayout.PlotPoint(ripe[0]));
            VerifyScreen(StandLayout.HarvestPanel, "Инструмент сбора не появился.");
            Tap(StandLayout.ToolPoint);
            VerifyScreen(StandLayout.HarvestReady, "Инструмент сбора недоступен.");
            Drag(StandLayout.PlotPoint(ripe[0]), StandLayout.PlotPoint(ripe[ripe.Count - 1]));
            VerifyScreen(StandLayout.Field, "После сбора экран фермы не распознан.");
            int collected = ReadCounter(StandLayout.StockCounter) - before;
            Require(collected > 0 && PlotIndices(StandLayout.RipePlot).Count == 0,
                "Силос заполнен или часть урожая осталась на грядках.");
            result.Harvested = collected;
        }

        private void SellSurplus()
        {
            Status("Проверка киоска");
            result.RemainingStock = ReadCounter(StandLayout.StockCounter);
            Tap(StandLayout.StallPoint);
            VerifyScreen(StandLayout.Shop, "Киоск не открылся.");
            for (int slot = 0; slot < StandLayout.MaxSlots; slot++)
            {
                Point point = StandLayout.SlotPoint(slot);
                if (!Same(Pixel(point), StandLayout.SoldSlot)) continue;
                int before = ReadCounter(StandLayout.CoinCounter);
                Tap(point);
                VerifyScreen(StandLayout.Shop, "После получения монет экран киоска изменился.");
                int difference = ReadCounter(StandLayout.CoinCounter) - before;
                Require(difference > 0 && !Same(Pixel(point), StandLayout.SoldSlot), "Получение монет не подтвердилось.");
                result.CoinsCollected += difference;
            }
            int batch = Math.Min(options.Batch, result.RemainingStock - options.Reserve);
            if (batch <= 0) throw new CycleStop("Запас пшеницы зарезервирован для следующего посева.");
            int free = -1;
            for (int slot = 0; slot < StandLayout.MaxSlots && free < 0; slot++)
                if (Same(Pixel(StandLayout.SlotPoint(slot)), StandLayout.FreeSlot)) free = slot;
            Require(free >= 0, "В киоске нет свободных слотов.");

            Status("Выставление пшеницы");
            Point selectedSlot = StandLayout.SlotPoint(free);
            Tap(selectedSlot);
            VerifyScreen(StandLayout.Listing, "Форма продажи не открылась.");
            Require(Same(Pixel(StandLayout.WheatPoint), StandLayout.WheatAvailable), "Пшеница недоступна для продажи.");
            Tap(StandLayout.WheatPoint);
            Require(Same(Pixel(StandLayout.WheatPoint), StandLayout.WheatSelected), "Выбор пшеницы не подтвердился.");
            SetCounter(StandLayout.AmountCounter, StandLayout.AmountMinus, StandLayout.AmountPlus, batch);
            SetCounter(StandLayout.PriceCounter, StandLayout.PriceMinus, StandLayout.PricePlus, options.Price);

            if (options.FreeAdvertisement && Same(Pixel(StandLayout.AdPoint), StandLayout.AdFree))
            {
                Tap(StandLayout.AdPoint);
                Require(Same(Pixel(StandLayout.AdPoint), StandLayout.AdSelected), "Бесплатное объявление не подтвердилось.");
                result.Advertised = true;
            }
            VerifyScreen(StandLayout.Listing, "Форма продажи изменилась перед подтверждением.");
            Require(Same(Pixel(StandLayout.ConfirmPoint), StandLayout.ConfirmEnabled)
                && ReadCounter(StandLayout.StockCounter) - batch >= options.Reserve
                && ReadCounter(StandLayout.AmountCounter) == batch
                && ReadCounter(StandLayout.PriceCounter) == options.Price, "Параметры продажи изменились.");
            Tap(StandLayout.ConfirmPoint);
            VerifyScreen(StandLayout.Shop, "Результат выставления не распознан.");
            Require(Same(Pixel(selectedSlot), StandLayout.OccupiedSlot)
                && ReadCounter(StandLayout.StockCounter) == result.RemainingStock - batch,
                "Выставление партии не подтверждено по киоску и остатку.");
            result.Listed = batch;
            result.RemainingStock -= batch;
        }

        private void SetCounter(Point counter, Point minus, Point plus, int goal)
        {
            for (int tries = 0; tries < 100; tries++)
            {
                VerifyScreen(StandLayout.Listing, "Форма продажи закрылась при настройке партии.");
                int current = ReadCounter(counter);
                if (current == goal) return;
                bool increase = current < goal;
                Tap(increase ? plus : minus);
                int next = ReadCounter(counter);
                Require(increase ? next > current && next <= goal : next < current && next >= goal,
                    "Счётчик не дошёл до заданного значения.");
            }
            throw new CycleStop("Подстройка счётчика превысила допустимое число шагов.");
        }

        private List<int> PlotIndices(Color color)
        {
            stop.ThrowIfCancellationRequested();
            using (Bitmap bitmap = stand.Capture())
                return Enumerable.Range(0, StandLayout.MaxPlots).Where(index =>
                {
                    Point point = StandLayout.PlotPoint(index);
                    return Same(bitmap.GetPixel(point.X, point.Y), color);
                }).ToList();
        }

        private Color Screen() { return Pixel(StandLayout.ScreenProbe); }
        private void VerifyScreen(Color color, string message) { Require(Same(Screen(), color), message); }
        private static bool Same(Color a, Color b) { return a.ToArgb() == b.ToArgb(); }

        private Color Pixel(Point point)
        {
            stop.ThrowIfCancellationRequested();
            using (Bitmap bitmap = stand.Capture()) return bitmap.GetPixel(point.X, point.Y);
        }

        private int ReadCounter(Point point)
        {
            stop.ThrowIfCancellationRequested();
            using (Bitmap bitmap = stand.Capture())
            {
                int value = StandLayout.ReadCounter(bitmap, point);
                Require(value >= 0, "Счётчик на экране не распознан.");
                return value;
            }
        }

        private void Tap(Point point) { stop.ThrowIfCancellationRequested(); stand.Tap(point); }
        private void Drag(Point from, Point to) { stop.ThrowIfCancellationRequested(); stand.Drag(from, to); }
        private void Status(string text) { stop.ThrowIfCancellationRequested(); report(text); }
        private static void Require(bool condition, string message) { if (!condition) throw new CycleStop(message); }
    }
}
