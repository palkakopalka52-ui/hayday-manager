using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FarmDesk
{
    internal static class SmokeTests
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

        public static int Run(string reportPath)
        {
            string path = Path.GetFullPath(reportPath);
            return Task.Run(() => RunCases(path)).GetAwaiter().GetResult();
        }

        private static int RunCases(string reportPath)
        {
            string folder = Path.GetDirectoryName(reportPath);
            Directory.CreateDirectory(folder);
            string preview = Path.Combine(folder, Path.GetFileNameWithoutExtension(reportPath) + "-stand.png");
            var log = new List<string> { "Локальная проверка FarmDesk", "Время запуска " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") };
            var time = Stopwatch.StartNew();
            int failures = 0;
            failures += RunCase(log, "Цикл на визуальном стенде", () => FullCycle(log, preview));
            failures += RunCase(log, "Отмена во время созревания", CancelDuringWait);
            failures += RunCase(log, "Нет семян", NoSeed);
            failures += RunCase(log, "Полный силос", FullSilo);
            failures += RunCase(log, "Перезапуск участка и занятое место продажи", OccupiedSlot);
            failures += RunCase(log, "Распознавание счётчика Windows", ReadCounter);
            failures += RunCase(log, "Сохранение карточек", () => Persistence(folder));
            failures += RunCase(log, "Инвентаризация BlueStacks", EnvironmentSummary);
            log.Add(Format("Итог · {0} сбоев · {1:F2} с", failures, time.Elapsed.TotalSeconds));
            File.WriteAllLines(reportPath, log, new UTF8Encoding(false));
            return failures == 0 ? 0 : 1;
        }

        private static int RunCase(List<string> log, string name, Func<string> run)
        {
            log.Add("");
            log.Add(name);
            try
            {
                log.Add("Прошло · " + run());
                return 0;
            }
            catch (Exception error)
            {
                log.Add("Сбой · " + error.GetType().Name + " · " + error.Message);
                return 1;
            }
        }

        private static string ReadCounter()
        {
            using (var bitmap = new Bitmap(175, 72))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Segoe UI", 31, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("781", font, Brushes.Black, 22, 15);
                var region = VisualRegion.FromRectangle(new Rectangle(15, 8, 95, 54), bitmap.Width, bitmap.Height);
                int number = new NumberReader().Read(bitmap, region, CancellationToken.None);
                if (number != 781) throw new InvalidOperationException("Ожидалось 781, распознано " + number + ".");
                return "781 прочитано из изображения";
            }
        }

        private static string FullCycle(List<string> log, string preview)
        {
            using (var stand = new CycleStand())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var options = new FarmOptions();
                CycleResult result;
                log.Add("Замеры процесса стенда · CPU в долях всех логических процессоров · рабочий набор в начале и конце этапа");
                using (var profile = new StageProfile(log))
                    result = new StandSolver(stand, options, stop.Token, profile.Change).RunAsync().GetAwaiter().GetResult();

                Require(result.Sown == 6 && result.Harvested == 12 && result.Replanted == 6,
                    "Ожидалось шесть посевов, двенадцать единиц урожая и шесть повторных посевов.");
                Require(result.Listed == options.Batch && result.RemainingStock == options.Reserve &&
                    result.CoinsCollected == 6 && result.Advertised, "Параметры партии, остаток, монеты или бесплатное объявление расходятся.");
                CheckFrame(stand, frame =>
                {
                    Require(ColorAt(frame, StandLayout.ScreenProbe, StandLayout.Shop) &&
                        ColorAt(frame, StandLayout.SlotPoint(0), StandLayout.OccupiedSlot), "Партия отсутствует в витрине.");
                    Require(StandLayout.ReadCounter(frame, StandLayout.StockCounter) == 6 &&
                        StandLayout.ReadCounter(frame, StandLayout.CoinCounter) == 6, "Счётчики итогового кадра расходятся.");
                    frame.Save(preview, ImageFormat.Png);
                });
                return Format("6 засеяно · 12 собрано · 6 пересеяно · {0} выставлено за {1} · остаток {2} · получено {3} монет · объявление бесплатно · {4:F2} с",
                    result.Listed, options.Price, result.RemainingStock, result.CoinsCollected, result.Duration.TotalSeconds);
            }
        }

        private static string CancelDuringWait()
        {
            using (var stand = new CycleStand())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                int waiting = 0;
                var clock = new Stopwatch();
                var solver = new StandSolver(stand, new FarmOptions(), stop.Token, step =>
                {
                    if (!step.StartsWith("Созревает", StringComparison.Ordinal)) return;
                    waiting++;
                    clock.Start();
                    stop.CancelAfter(120);
                });

                try
                {
                    solver.RunAsync().GetAwaiter().GetResult();
                    throw new InvalidOperationException("Сценарий завершился после запроса отмены.");
                }
                catch (OperationCanceledException)
                {
                    if (waiting != 1 || !stop.IsCancellationRequested) throw;
                    clock.Stop();
                }

                CheckFrame(stand, frame => Require(PlotCount(frame, StandLayout.SeededPlot) == 6 &&
                    StandLayout.ReadCounter(frame, StandLayout.StockCounter) == 6, "Отмена изменила засев или запас."));
                return Format("остановка через {0:F2} с от начала ожидания · шесть посевов сохранены", clock.Elapsed.TotalSeconds);
            }
        }

        private static string NoSeed()
        {
            using (var stand = new CycleStand(0, 80, 6, 3, 1))
            {
                string reason = ExpectStop(stand, "семян");
                CheckFrame(stand, frame => Require(PlotCount(frame, StandLayout.EmptyPlot) == 6 &&
                    StandLayout.ReadCounter(frame, StandLayout.StockCounter) == 0, "Сценарий изменил участок без семян."));
                return reason;
            }
        }

        private static string FullSilo()
        {
            using (var stand = new CycleStand(7, 7, 6, 3, 1))
            {
                string reason = ExpectStop(stand, "силос");
                CheckFrame(stand, frame => Require(PlotCount(frame, StandLayout.RipePlot) == 3 &&
                    StandLayout.ReadCounter(frame, StandLayout.StockCounter) == 7, "Остаток урожая не соответствует полной ёмкости."));
                return reason + " В силосе 7, созрели ещё 3 грядки.";
            }
        }

        private static string OccupiedSlot()
        {
            using (var stand = new CycleStand(18, 80, 6, 1, 1, 180, false))
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var options = new FarmOptions { FreeAdvertisement = false };
                CycleResult first = new StandSolver(stand, options, stop.Token, delegate { }).RunAsync().GetAwaiter().GetResult();
                Require(first.Listed == 6 && first.RemainingStock == 12 && !first.Advertised, "Первая партия отсутствует или рекламируется.");
                stand.RestartView();
                string reason = ExpectStop(stand, "свободных слотов");
                CheckFrame(stand, frame => Require(ColorAt(frame, StandLayout.SlotPoint(0), StandLayout.OccupiedSlot) &&
                    StandLayout.ReadCounter(frame, StandLayout.StockCounter) == 18, "Предыдущая партия или новый запас потеряны."));
                return reason + " Первая партия сохранилась, запас после пересева 18.";
            }
        }

        private static string ExpectStop(CycleStand stand, string reasonPart)
        {
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try
                {
                    new StandSolver(stand, new FarmOptions(), stop.Token, delegate { }).RunAsync().GetAwaiter().GetResult();
                }
                catch (CycleStop error)
                {
                    Require(error.Message.IndexOf(reasonPart, StringComparison.OrdinalIgnoreCase) >= 0,
                        "Получена другая причина остановки · " + error.Message);
                    return error.Message;
                }
            }
            throw new InvalidOperationException("Остановка сценария не произошла.");
        }

        private static string Persistence(string folder)
        {
            string testFolder = Path.Combine(folder, "smoke-data-" + Guid.NewGuid().ToString("N"));
            var store = new DataStore(testFolder);
            ManagerSettings settings = store.Load();
            Require(settings.ParallelLimit == 1 && settings.Cards.Count == 0, "Неверное исходное состояние карточек.");

            FarmCard first = settings.Attach("Pie64");
            string createdAt = first.CreatedUtc;
            Require(Object.ReferenceEquals(first, settings.Attach("Pie64")), "Повторное подключение создало дубликат.");
            first.Comment = "Проверка UTF-8 · Поле №1";
            first.Options.Batch = 7;
            first.LastOperation = "В очереди";
            settings.ParallelLimit = 2;
            store.Save(settings);

            ManagerSettings loaded = new DataStore(testFolder).Load();
            FarmCard same = loaded.Attach("Pie64");
            Require(loaded.Cards.Count == 1 && same.Number == 1 && same.CreatedUtc == createdAt &&
                same.Comment == first.Comment && same.Options.Batch == 7 && same.LastOperation == "В очереди" &&
                loaded.ParallelLimit == 2, "Карточка или параметры исчезли после загрузки.");
            FarmCard other = loaded.Attach("Pie64_2");
            Require(other.Number == 2 && store.DeviceFolder(first.InstanceId) != store.DeviceFolder(other.InstanceId),
                "Второе устройство не получило свой номер и каталог.");
            store.Save(loaded);
            Require(new DataStore(testFolder).Load().Cards.Count == 2, "Повторная запись карточек не сохранилась.");
            return "два стабильных ID · номер, дата, комментарий, операция, настройки и лимит восстановлены";
        }

        private static string EnvironmentSummary()
        {
            BlueStacksService service = BlueStacksService.Discover();
            if (service.PlayerPath == null) return "установка не обнаружена в этой среде";

            List<DeviceInfo> devices = service.ReadDevices();
            int active = service.ReadPlayers().Count;
            return Format("версия {0} · запущено {1} · {2}", service.Version, active, devices.Count == 0
                ? "экземпляров в конфигурации нет"
                : string.Join(" / ", devices.Select(device => Format("{0} · {1}×{2} · {3} FPS · {4} МиБ · {5} ядер",
                    device.Id, device.Width, device.Height, device.Fps, device.RamMiB, device.Cores))));
        }

        private static void CheckFrame(CycleStand stand, Action<Bitmap> check)
        {
            using (Bitmap frame = stand.Capture()) check(frame);
        }

        private static int PlotCount(Bitmap frame, Color color)
        {
            return Enumerable.Range(0, StandLayout.MaxPlots).Count(i => ColorAt(frame, StandLayout.PlotPoint(i), color));
        }

        private static bool ColorAt(Bitmap frame, Point pixel, Color color)
        {
            return frame.GetPixel(pixel.X, pixel.Y).ToArgb() == color.ToArgb();
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }

        private static string Format(string pattern, params object[] values)
        {
            return String.Format(Russian, pattern, values);
        }

        private sealed class StageProfile : IDisposable
        {
            private readonly List<string> log;
            private readonly Process process = Process.GetCurrentProcess();
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private string name;
            private TimeSpan fromWall;
            private TimeSpan fromCpu;
            private long fromMemory;

            public StageProfile(List<string> log)
            {
                this.log = log;
            }

            public void Change(string next)
            {
                process.Refresh();
                TimeSpan wall = clock.Elapsed;
                TimeSpan cpu = process.TotalProcessorTime;
                long memory = process.WorkingSet64;
                if (name != null)
                {
                    double elapsed = (wall - fromWall).TotalSeconds;
                    double processor = elapsed > 0 ? (cpu - fromCpu).TotalSeconds * 100 / elapsed / Environment.ProcessorCount : 0;
                    log.Add(Format("  {0} · {1:F2} с · CPU {2:F1}% · {3:F1} → {4:F1} МиБ", name, elapsed, processor,
                        fromMemory / 1048576d, memory / 1048576d));
                }
                name = next;
                fromWall = wall;
                fromCpu = cpu;
                fromMemory = memory;
            }

            public void Dispose()
            {
                try { Change(null); }
                finally { process.Dispose(); }
            }
        }
    }
}
