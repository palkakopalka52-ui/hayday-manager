using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FarmDesk
{
    internal sealed class LiveCycle : IDisposable
    {
        internal const string FieldComplete = "Посев и сбор завершены";
        internal const string HarvestComplete = "Сбор завершён";
        private readonly DesktopSurface surface;
        private readonly FarmCalibration calibration;
        private readonly FarmOptions options;
        private readonly NumberReader numbers;
        private readonly CancellationToken stop;
        private readonly Action<string> report;
        private readonly bool fieldOnly;
        private readonly Point[] points;
        private DateTime nextAdvertisementUtc;
        internal DateTime NextAdvertisementUtc { get { return nextAdvertisementUtc; } }
        private Bitmap lastScreen;
        private Point cameraOffset;
        private Point cameraOrigin;
        private double cameraScale = 1;
        private int reconnects;
        private bool capacityExceeded;
        private string currentStage = "Ищу ферму и грядки";

        public LiveCycle(DesktopSurface surface, FarmCalibration calibration, FarmOptions options,
            NumberReader numbers, CancellationToken stop, Action<string> report, bool fieldOnly = false,
            DateTime nextAdvertisementUtc = default(DateTime))
        {
            this.surface = surface;
            this.calibration = calibration;
            this.options = options;
            this.numbers = numbers;
            this.stop = stop;
            this.report = report;
            this.fieldOnly = fieldOnly;
            this.nextAdvertisementUtc = nextAdvertisementUtc;
            points = calibration.PlotPoints();
        }

        public Bitmap LastScreen()
        {
            return lastScreen == null ? null : (Bitmap)lastScreen.Clone();
        }

        public async Task<CycleResult> RunAsync(bool requireEmptyStart = true, bool sellPlantedOnResume = false)
        {
            string[] missing = calibration.Missing(fieldOnly);
            if (missing.Length != 0) throw new CycleStop("Нужна калибровка · " + String.Join(", ", missing.Take(5)) + ".");
            ValidateOptions(options, points.Length, fieldOnly);
            var timer = Stopwatch.StartNew();
            var result = new CycleResult();
            await CloseFullSilo().ConfigureAwait(false);
            await CloseNewspaper().ConfigureAwait(false);
            bool shopOpen, listingOpen;
            using (Bitmap current = Capture())
            {
                listingOpen = Matches(current, "listing") && Matches(current, "listingClose");
                shopOpen = Matches(current, "shop");
            }
            if (requireEmptyStart && (listingOpen || shopOpen))
                throw new CycleStop("Перед стартом закрой киоск и покажи пустое поле.");
            if (listingOpen)
            {
                await CloseListing().ConfigureAwait(false);
                shopOpen = true;
            }
            if (shopOpen)
            {
                await ReturnToFarm().ConfigureAwait(false);
                shopOpen = false;
            }

            if (!shopOpen)
            {
                Stage("Ищу ферму и грядки");
                await WaitForFarm().ConfigureAwait(false);
                await ClearPlotSelection().ConfigureAwait(false);
                bool alreadySown = false;
                int emptyBeforeHarvest = 0;
                if (!fieldOnly && !requireEmptyStart)
                {
                    using (Bitmap frame = Capture())
                    {
                        Plot[] observed = ReadPlots(frame);
                        alreadySown = sellPlantedOnResume && observed.All(plot => plot.State == null || plot.State == "seeded") &&
                            observed.Count(plot => plot.State == "seeded") >= Math.Max(1, points.Length - 3);
                        if (observed.All(plot => plot.State == "empty" || plot.State == "ripe") &&
                            observed.Any(plot => plot.State == "ripe"))
                            emptyBeforeHarvest = observed.Count(plot => plot.State == "empty");
                    }
                }
                if (!alreadySown && emptyBeforeHarvest == 0)
                {
                    result.Sown = await WorkPlots("empty", "seed", "Посев пшеницы", requireEmpty: requireEmptyStart).ConfigureAwait(false);
                    Stage("Жду созревания");
                    if (result.Sown > 0) await Task.Delay(TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
                    await WaitForRipe().ConfigureAwait(false);
                }
                if (fieldOnly)
                {
                    result.Harvested = await HarvestPlots(points.Length, new HashSet<int>()).ConfigureAwait(false);
                    stop.ThrowIfCancellationRequested();
                    if (result.Harvested != points.Length)
                        throw new CycleStop("Сбор подтвердился не на всех выбранных грядках.");
                    result.Duration = timer.Elapsed;
                    result.FinalStatus = result.Sown > 0 ? FieldComplete : HarvestComplete;
                    return result;
                }
                if (!alreadySown) await HarvestAndReplant(result, emptyBeforeHarvest).ConfigureAwait(false);
                await ConfirmWholeFieldSown().ConfigureAwait(false);
            }
            await OpenShop().ConfigureAwait(false);
            result.RemainingStock = -1;
            bool shelfFull = false;
            while (result.RemainingStock != 0)
            {
                Point? slot = await OpenListing(result, false).ConfigureAwait(false);
                if (!slot.HasValue) { shelfFull = true; break; }
                int sold = await PublishWheat(result, slot.Value, 0).ConfigureAwait(false);
                if (sold != 0) continue;
                await CloseListing().ConfigureAwait(false);
                break;
            }
            result.Duration = timer.Elapsed;
            result.FinalStatus = shelfFull ? "Цикл завершён · киоск заполнен" : "Цикл завершён";
            return result;
        }

        internal static void ValidateOptions(FarmOptions selected, int plotCount, bool fieldOnly)
        {
            if (!fieldOnly) selected.Validate(plotCount);
            else if (selected.WaitIntervalSeconds < 2 || selected.WaitIntervalSeconds > 30)
                throw new CycleStop("Интервал проверки должен быть от 2 до 30 секунд.");
        }

        private async Task WaitForFarm()
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 60)
            {
                using (Bitmap bitmap = Capture())
                {
                    if (IsFarmScreen(bitmap))
                    {
                        try { FindOffset(bitmap); return; }
                        catch (CameraNotFound) { if (timer.Elapsed.TotalSeconds > 10) throw; }
                    }
                    if (Matches(bitmap, "offline")) await Reconnect(bitmap).ConfigureAwait(false);
                    else if (timer.Elapsed.TotalSeconds > 8 && !Matches(bitmap, "loading"))
                        throw new CycleStop("Неизвестный экран при загрузке фермы.");
                }
                await Task.Delay(800, stop).ConfigureAwait(false);
            }
            throw new CycleStop("Игровой экран не распознан за минуту.");
        }

        private async Task WaitForRipe()
        {
            var timer = Stopwatch.StartNew();
            var ripe = new HashSet<int>();
            bool paused = false;
            bool clearedMatureMenu = false;
            int mostlyRipeFrames = 0;
            while (timer.Elapsed.TotalMinutes < 8)
            {
                if (!surface.IsForeground())
                {
                    surface.ClientBounds();
                    if (!paused)
                    {
                        Stage("Жду возврата в BlueStacks");
                        timer.Stop();
                        ripe.Clear();
                        paused = true;
                    }
                    await Task.Delay(500, stop).ConfigureAwait(false);
                    continue;
                }
                if (paused)
                {
                    Stage("Жду созревания");
                    timer.Start();
                    paused = false;
                }
                using (Bitmap bitmap = Capture())
                {
                    if (Matches(bitmap, "offline"))
                    {
                        await Reconnect(bitmap).ConfigureAwait(false);
                        await WaitForFarm().ConfigureAwait(false);
                        ripe.Clear();
                    }
                    else
                    {
                        Plot[] visible = null;
                        try { visible = ReadPlots(bitmap); }
                        catch (CameraNotFound) { ripe.Clear(); }
                        foreach (Plot plot in visible ?? new Plot[0])
                        {
                            if (plot.State == "ripe") ripe.Add(plot.Index);
                            else if (plot.State != null) ripe.Remove(plot.Index);
                        }
                        if (ripe.Count == points.Length) return;
                        bool mostlyRipe = visible != null && points.Length >= 10 &&
                            visible.Count(plot => plot.State == "ripe") >= points.Length - 2 &&
                            visible.All(plot => plot.State == null || plot.State == "ripe");
                        mostlyRipeFrames = mostlyRipe ? mostlyRipeFrames + 1 : 0;
                        if (mostlyRipeFrames >= 2) return;
                        if (ripe.Count > 0 && !clearedMatureMenu)
                        {
                            await ClearPlotSelection().ConfigureAwait(false);
                            clearedMatureMenu = true;
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(options.WaitIntervalSeconds), stop).ConfigureAwait(false);
            }
            throw new CycleStop("Грядки не распознаны как спелые за восемь минут.");
        }

        private async Task<int> WorkPlots(string state, string tool, string operation, int limit = Int32.MaxValue,
            bool requireEmpty = false, HashSet<int> visited = null)
        {
            var visible = new Plot[points.Length];
            int samples = 0;
            await WaitFor(marked =>
            {
                foreach (Plot plot in ReadPlots(marked))
                    if (plot.State != null) visible[plot.Index] = plot;
                samples++;
                if (visible.All(plot => plot != null)) return true;
                return !requireEmpty && points.Length >= 10 && visible.Count(plot => plot != null) >= points.Length - 2
                    && (visible.Any(plot => plot != null && plot.State == state) || (state == "empty" && samples >= 5));
            }, "Состояние участка закрыто или не распознано.", 30).ConfigureAwait(false);
            if (requireEmpty && visible.Any(plot => plot.State != "empty"))
                throw new CycleStop("Перед стартом очисти все грядки в выбранной области.");
            Plot[] candidates = visible.Where(plot => plot != null && plot.State == state &&
                (visited == null || !visited.Contains(plot.Index))).ToArray();
            Plot[] initial = candidates.Take(limit).ToArray();
            if (initial.Length == 0) return 0;
            Stage(operation);
            using (Bitmap before = Capture())
                if (!ScreenVision.Matches(before, Marker(tool), 11))
                {
                    Point point = PlotPoint(before, initial[0].Index, FindOffset(before));
                    await Tap(AimPlot(before, state, point)).ConfigureAwait(false);
                }
            int first = initial[0].Index;
            bool alreadyEmpty = false;
            await WaitFor(marked =>
            {
                Point point = PlotPoint(marked, first, FindOffset(marked));
                if (LocateTool(marked, tool, point).HasValue) return true;
                alreadyEmpty = visited != null && LocateTool(marked, "seed", point).HasValue;
                return alreadyEmpty;
            },
                "Инструмент грядки не распознан. Проверь точку его захвата.").ConfigureAwait(false);
            if (alreadyEmpty)
            {
                visited.Add(first);
                await ClearPlotSelection().ConfigureAwait(false);
                return 0;
            }

            Point[] route;
            using (Bitmap bitmap = Capture())
            {
                var marker = Marker(tool);
                Rectangle expectedGrip = ScreenVision.Base(bitmap, marker);
                Point cameraOffset = FindOffset(bitmap);
                Rectangle? grip = LocateTool(bitmap, tool, PlotPoint(bitmap, first, cameraOffset));
                if (!grip.HasValue) throw new CycleStop("Инструмент исчез перед жестом.");
                if (tool != "sickle") grip = ObjectVision.Find(bitmap, marker, grip.Value, 8, stop) ?? grip;
                var popupOffset = new Point(grip.Value.X - expectedGrip.X, grip.Value.Y - expectedGrip.Y);
                if (tool == "seed" && calibration.Counters.ContainsKey("seedStock") &&
                    ReadNumber(bitmap, "seedStock", popupOffset) < initial.Length)
                    throw new CycleStop("Недостаточно семян для выбранных грядок.");
                route = new[] { ScreenVision.Center(grip.Value) }
                    .Concat(initial.Select(plot => AimPlot(bitmap, state, PlotPoint(bitmap, plot.Index, cameraOffset)))).ToArray();
            }
            await surface.DragAsync(calibration.Viewport, route, stop).ConfigureAwait(false);
            await CloseNewspaper().ConfigureAwait(false);
            if (tool == "sickle")
            {
                await Task.Delay(240, stop).ConfigureAwait(false);
                capacityExceeded = await CloseFullSilo().ConfigureAwait(false);
            }
            await ClearPlotSelection().ConfigureAwait(false);
            var changed = new HashSet<int>();
            int verificationFrames = 0;
            string expected = state == "empty" ? "seeded" : "empty";
            await WaitFor(marked =>
            {
                Plot[] after = ReadPlots(marked);
                verificationFrames++;
                foreach (Plot plot in candidates)
                {
                    if (after[plot.Index].State == expected ||
                        (state == "empty" && after[plot.Index].State == "ripe")) changed.Add(plot.Index);
                    else if (after[plot.Index].State != null) changed.Remove(plot.Index);
                }
                return capacityExceeded || initial.All(plot => changed.Contains(plot.Index)) ||
                    (visited != null && changed.Count > 0 && verificationFrames >= 6);
            }, state == "empty" ? "Посев не подтверждён по всем выбранным грядкам."
                : "Сбор не подтверждён по всем выбранным грядкам.", 30, tool == "sickle").ConfigureAwait(false);
            if (visited != null) visited.UnionWith(changed);
            return changed.Count;
        }

        private async Task<bool> CloseFullSilo()
        {
            using (Bitmap frame = Capture())
                if (!Matches(frame, "full")) return false;
            await TapMark("fullClose").ConfigureAwait(false);
            await WaitFor(IsFarmScreen, "После закрытия силоса ферма не видна.").ConfigureAwait(false);
            return true;
        }

        private async Task CloseNewspaper()
        {
            using (Bitmap frame = Capture())
                if (!Matches(frame, "newspaper") || !Matches(frame, "newspaperClose")) return;
            await TapMark("newspaperClose").ConfigureAwait(false);
            await WaitFor(IsFarmScreen, "После закрытия газеты ферма не видна.").ConfigureAwait(false);
        }

        private Point AimPlot(Bitmap image, string state, Point point)
        {
            VisualMarker tile = Marker(state);
            Rectangle expected = ScaleAroundCenter(ScreenVision.At(image, tile, point));
            if (ScreenVision.Distance(image, tile, expected) <= 20) return point;
            if (state == "empty")
            {
                var left = new Point(point.X - 8, point.Y + 3);
                if (ReadCropColor(image, left) == "empty") return left;
            }
            Rectangle? detected = ScreenVision.Locate(image, tile, expected, 12, 24);
            return detected.HasValue ? ScreenVision.Center(detected.Value) : point;
        }

        private async Task ClearPlotSelection()
        {
            VisualMarker marker = Marker("clearSelection");
            if (marker == null) return;
            await Task.Delay(280, stop).ConfigureAwait(false);
            using (Bitmap frame = Capture())
            {
                if (Matches(frame, "full")) return;
                CheckObstacles(frame);
                Point offset;
                try { offset = FindOffset(frame); }
                catch (CameraNotFound) { return; }
                Rectangle ground = Transform(ScreenVision.Base(frame, marker), offset);
                Rectangle? visibleGround = ScreenVision.Locate(frame, marker, ground, 5, 18);
                if (!visibleGround.HasValue) return;
                await Tap(ScreenVision.Center(visibleGround.Value)).ConfigureAwait(false);
            }
            await CloseNewspaper().ConfigureAwait(false);
        }

        private async Task OpenShop()
        {
            Stage("Открываю придорожный киоск");
            Point storefront;
            Point boothOffset;
            using (Bitmap bitmap = Capture())
            {
                Point offset = FindOffset(bitmap);
                VisualMarker stall = Marker("stall");
                Rectangle reference = ScreenVision.Base(bitmap, stall);
                Rectangle target = Transform(reference, offset);
                Rectangle nearby = new Rectangle(ScreenVision.Center(target).X - reference.Width / 2,
                    ScreenVision.Center(target).Y - reference.Height / 2, reference.Width, reference.Height);
                Rectangle? detected = ObjectVision.Find(bitmap, stall, nearby, 65, stop, "stall")
                    ?? ScreenVision.Locate(bitmap, stall, target, 65, 19)
                    ?? ScreenVision.Locate(bitmap, stall, nearby, 65, 19)
                    ?? ScreenVision.Locate(bitmap, stall, nearby, 10, 33);
                if (!detected.HasValue)
                    throw new CycleStop("Киоск потерян из вида.");
                storefront = ScreenVision.Center(detected.Value);
                Point origin = ScreenVision.Center(Transform(reference, Point.Empty));
                boothOffset = new Point(storefront.X - origin.X, storefront.Y - origin.Y);
            }
            await Tap(storefront).ConfigureAwait(false);
            await WaitFor(marked => Matches(marked, "shop"), "Собственный киоск не распознан.").ConfigureAwait(false);
            cameraOffset = boothOffset;
        }

        private async Task<Point?> OpenListing(CycleResult result, bool waitForSlot)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                Point? free = await CollectAndFindFree(result).ConfigureAwait(false);
                if (free.HasValue)
                {
                    await Tap(free.Value).ConfigureAwait(false);
                    await WaitFor(marked => Matches(marked, "listing"), "Форма продажи не распознана.").ConfigureAwait(false);
                    return free.Value;
                }
                if (!waitForSlot) return null;
                Stage("Жду свободную ячейку киоска");
                await Task.Delay(TimeSpan.FromSeconds(options.WaitIntervalSeconds), stop).ConfigureAwait(false);
            } while (timer.Elapsed.TotalMinutes < 3);
            throw new CycleStop("За три минуты не появилось распознанных свободных ячеек киоска.");
        }

        private async Task ReturnToFarm()
        {
            await TapMark("shopClose").ConfigureAwait(false);
            await WaitForFarm().ConfigureAwait(false);
        }

        private async Task HarvestAndReplant(CycleResult result, int emptyBeforeHarvest)
        {
            const int wheatPerPlot = 2;
            int remaining = points.Length - emptyBeforeHarvest;
            var visited = new HashSet<int>();
            bool readSilo = Marker("shopClose") != null && Marker("listingClose") != null &&
                calibration.Counters.ContainsKey("silo");
            int free = readSilo ? 0 : remaining * wheatPerPlot;
            while (remaining > 0)
            {
                if (free < wheatPerPlot)
                    free = await HarvestSpace(result, remaining * wheatPerPlot).ConfigureAwait(false);
                if (free < wheatPerPlot) throw new CycleStop("В силосе нет места для урожая при заданном запасе семян.");
                capacityExceeded = false;
                int count = await HarvestPlots(Math.Min(remaining, free / wheatPerPlot), visited).ConfigureAwait(false);
                bool wasFull = capacityExceeded;
                capacityExceeded = false;
                if (count == 0 && wasFull)
                {
                    free = 0;
                    continue;
                }
                if (count == 0)
                {
                    result.Replanted += await WorkPlots("empty", "seed", "Повторный посев").ConfigureAwait(false);
                    break;
                }
                result.Harvested += count;
                remaining = Math.Max(0, remaining - count);
                int planted = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int next = await WorkPlots("empty", "seed", "Повторный посев").ConfigureAwait(false);
                    if (next == 0) break;
                    planted += next;
                }
                bool uncertainCapacity = planted != count + emptyBeforeHarvest;
                emptyBeforeHarvest = 0;
                result.Replanted += planted;
                free = wasFull || uncertainCapacity ? 0 : free - count * wheatPerPlot + planted;
            }
        }

        private async Task ConfirmWholeFieldSown()
        {
            var planted = new HashSet<int>();
            await WaitFor(frame =>
            {
                foreach (Plot plot in ReadPlots(frame))
                {
                    if (plot.State == "seeded") planted.Add(plot.Index);
                    else if (plot.State != null) planted.Remove(plot.Index);
                }
                return planted.Count == points.Length;
            }, "Перед продажей всего остатка не удалось подтвердить посев всех грядок.", 45).ConfigureAwait(false);
        }

        private async Task<int> HarvestPlots(int limit, HashSet<int> visited)
        {
            int count = 0;
            while (count < limit)
            {
                int before = visited.Count;
                int harvested = 0;
                bool fullDuringGroup = false;
                try
                {
                    harvested = await WorkPlots("ripe", "sickle", "Сбор урожая",
                        limit - count, visited: visited).ConfigureAwait(false);
                }
                catch (SiloFull) { fullDuringGroup = true; }
                if (fullDuringGroup)
                {
                    capacityExceeded = await CloseFullSilo().ConfigureAwait(false);
                    if (!capacityExceeded) throw new SiloFull();
                    break;
                }
                if (harvested == 0 && visited.Count == before) break;
                count += harvested;
                if (capacityExceeded) break;
            }
            return count;
        }

        private async Task<int> HarvestSpace(CycleResult result, int required)
        {
            await OpenShop().ConfigureAwait(false);
            Point? slot = await OpenListing(result, true).ConfigureAwait(false);
            int free;
            using (Bitmap bitmap = Capture())
            {
                if (!Matches(bitmap, "listing")) throw new CycleStop("Форма продажи закрылась при проверке силоса.");
                Tuple<int, int> capacity = numbers.ReadCapacity(bitmap, calibration.Counters["silo"], stop);
                free = capacity.Item2 - capacity.Item1;
            }
            while (true)
            {
                if (free >= required) { await CloseListing().ConfigureAwait(false); break; }
                Stage("Освобождаю место в силосе для урожая");
                int sold = await PublishWheat(result, slot.Value, options.Reserve).ConfigureAwait(false);
                if (sold == 0) { await CloseListing().ConfigureAwait(false); break; }
                free += sold;
                if (free >= required) break;
                slot = await OpenListing(result, free < 2).ConfigureAwait(false);
                if (!slot.HasValue) break;
            }
            await ReturnToFarm().ConfigureAwait(false);
            return free;
        }

        private async Task CloseListing()
        {
            await TapMark("listingClose").ConfigureAwait(false);
            await WaitFor(marked => Matches(marked, "shop"), "После закрытия формы не виден киоск.").ConfigureAwait(false);
        }

        private async Task<int> PublishWheat(CycleResult result, Point slot, int reserve)
        {
            Stage("Настраиваю партию и цену");
            using (Bitmap frame = Capture())
            {
                if (!Matches(frame, "listing")) throw new CycleStop("Форма продажи не распознана.");
                surface.MovePointer(calibration.Viewport, new Point(443, 350));
            }
            await Task.Delay(260, stop).ConfigureAwait(false);
            using (Bitmap bitmap = Capture())
            {
                if (!Matches(bitmap, "listing")) throw new CycleStop("Форма продажи не распознана.");
                VisualMarker wheat = Marker("wheat");
                Rectangle expected = ScreenVision.Base(bitmap, wheat);
                Rectangle? found = ObjectVision.Find(bitmap, wheat, expected, 12, stop);
                if (!found.HasValue && !Matches(bitmap, "wheat")) throw new CycleStop("Пшеница в силосе не найдена.");
                await Tap(ScreenVision.Center(found ?? expected)).ConfigureAwait(false);
            }
            await WaitFor(marked => Matches(marked, "listing") && Matches(marked, "selected") && Matches(marked, "coins"),
                "Не подтверждена выбранная пшеница или цена в монетах.").ConfigureAwait(false);
            int amount;
            using (Bitmap bitmap = Capture())
            {
                int stock = ReadNumber(bitmap, "stock");
                amount = Math.Min(options.Batch, stock - reserve);
                if (amount < 1)
                {
                    if (Marker("listingClose") == null) throw new CycleStop("Пшеница нужна для следующего посева.");
                    result.RemainingStock = stock;
                    return 0;
                }
            }
            await SetNumber("amount", "amountDown", "amountUp", amount).ConfigureAwait(false);
            await SetPrice(options.Price).ConfigureAwait(false);
            bool advertised = await ChooseAdvertisement().ConfigureAwait(false);

            Point submit = Point.Empty;
            await WaitFor(bitmap =>
            {
                RequireListing(bitmap);
                if (!Matches(bitmap, "submit")
                    || (advertised ? !AdvertisementIsEnabled(bitmap) || !AdvertisementIsFree(bitmap) : !AdvertisementIsOff(bitmap)))
                    return false;
                int remaining = ReadNumber(bitmap, "stock") - amount;
                if (remaining < reserve || ReadNumber(bitmap, "amount") != amount || ReadNumber(bitmap, "price") != options.Price)
                    return false;
                result.RemainingStock = remaining;
                submit = ScreenVision.Center(ScreenVision.Base(bitmap, Marker("submit")));
                return true;
            }, "Кнопка выставления или параметры партии закрыты либо не подтвердились.", 55).ConfigureAwait(false);
            Stage("Выставляю пшеницу за монеты");
            await Tap(submit).ConfigureAwait(false);
            int confirmed = 0;
            await WaitFor(marked =>
            {
                bool occupied = Marker("occupied") == null
                    ? !SlotMatches(marked, "free", slot)
                    : SlotMatches(marked, "occupied", slot) || IsSold(marked, slot);
                if (Matches(marked, "shop") && occupied) return ++confirmed >= 2;
                confirmed = 0;
                return false;
            },
                "Выставление партии не подтвердилось. Проверь киоск перед повторным запуском.").ConfigureAwait(false);
            result.Listed += amount;
            result.Listings++;
            result.Advertised |= advertised;
            if (advertised) nextAdvertisementUtc = DateTime.UtcNow.AddMinutes(5);
            return amount;
        }

        private async Task<Point?> CollectAndFindFree(CycleResult result)
        {
            foreach (VisualRegion slot in calibration.Slots)
            {
                Point point = ScreenVision.Center(slot.ToRectangle(DesktopSurface.FrameWidth, DesktopSurface.FrameHeight));
                bool sold;
                using (Bitmap bitmap = Capture()) sold = Matches(bitmap, "shop") && IsSold(bitmap, point);
                if (!sold) continue;
                Stage("Забираю монеты за проданную партию");
                await Tap(point).ConfigureAwait(false);
                await WaitFor(marked => Matches(marked, "shop") && SlotMatches(marked, "free", point),
                    "Получение монет не подтвердилось.").ConfigureAwait(false);
                result.CollectedSales++;
            }
            using (Bitmap bitmap = Capture())
            {
                if (!Matches(bitmap, "shop")) throw new CycleStop("Экран киоска изменился.");
                foreach (VisualRegion slot in calibration.Slots)
                {
                    Point point = ScreenVision.Center(slot.ToRectangle(bitmap.Width, bitmap.Height));
                    if (SlotMatches(bitmap, "free", point)) return point;
                }
            }
            return null;
        }

        private async Task SetNumber(string counter, string minus, string plus, int goal)
        {
            for (int step = 0; step < 40; step++)
            {
                int current;
                using (Bitmap bitmap = Capture())
                {
                    RequireListing(bitmap);
                    current = ReadNumber(bitmap, counter);
                }
                if (current == goal) return;
                bool increase = current < goal;
                await TapMark(increase ? plus : minus).ConfigureAwait(false);
                bool changed = false;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    await Task.Delay(120, stop).ConfigureAwait(false);
                    using (Bitmap bitmap = Capture())
                    {
                        RequireListing(bitmap);
                        int next = ReadNumber(bitmap, counter);
                        if (next == current) continue;
                        if (increase ? next < current || next > goal : next > current || next < goal)
                            throw new CycleStop("Счётчик перескочил заданное значение.");
                        changed = true;
                        break;
                    }
                }
                if (!changed) throw new CycleStop("Счётчик остановился.");
            }
            throw new CycleStop("Слишком много изменений счётчика. Проверь размер партии и цену.");
        }

        private async Task SetPrice(int goal)
        {
            bool useMinimum;
            using (Bitmap bitmap = Capture())
            {
                RequireListing(bitmap);
                useMinimum = goal <= 2 && Matches(bitmap, "priceMin") && ReadNumber(bitmap, "price") > goal;
            }
            if (useMinimum)
            {
                await TapMark("priceMin").ConfigureAwait(false);
                await WaitFor(marked =>
                {
                    RequireListing(marked);
                    return ReadNumber(marked, "price") == 1;
                }, "Минимальная цена в одну монету не подтвердилась.").ConfigureAwait(false);
            }
            await SetNumber("price", "priceDown", "priceUp", goal).ConfigureAwait(false);
        }

        private async Task<bool> ChooseAdvertisement()
        {
            using (Bitmap bitmap = Capture())
            {
                RequireListing(bitmap);
                bool free = AdvertisementIsFree(bitmap);
                bool enabled = AdvertisementIsEnabled(bitmap);
                bool wantAd = options.FreeAdvertisement && DateTime.UtcNow >= nextAdvertisementUtc;
                if (wantAd && free && enabled) return true;
                if (wantAd && free && AdvertisementIsOff(bitmap) && Marker("adOn") != null)
                {
                    Stage("Включаю бесплатное объявление");
                    await Tap(ScreenVision.Center(ScreenVision.Base(bitmap, Marker("adIcon") ?? Marker("adOff")))).ConfigureAwait(false);
                    await WaitFor(marked => Matches(marked, "listing") && AdvertisementIsEnabled(marked) && AdvertisementIsFree(marked),
                        "Бесплатное объявление не подтверждено.").ConfigureAwait(false);
                    return true;
                }
                if (AdvertisementIsOff(bitmap)) return false;
                if (!free || !enabled) throw new CycleStop("Неясно, выключена ли реклама и бесплатна ли она.");
            }
            await TapMark("adOn").ConfigureAwait(false);
            await WaitFor(marked => Matches(marked, "listing") && AdvertisementIsOff(marked),
                "Отключение объявления не подтвердилось.").ConfigureAwait(false);
            return false;
        }

        private bool AdvertisementIsFree(Bitmap bitmap)
        {
            return ScreenVision.Matches(bitmap, Marker("adFree"), 10) &&
                ScreenVision.Matches(bitmap, Marker("adFreePanel"), 8);
        }

        private bool AdvertisementIsOff(Bitmap bitmap)
        {
            return !AdvertisementIsEnabled(bitmap) && (Matches(bitmap, "adOff") || Matches(bitmap, "adUnavailable")
                || (AdvertisementIsFree(bitmap) && CountAdvertisementPixels(bitmap, false) >= 35));
        }

        private bool AdvertisementIsEnabled(Bitmap bitmap)
        {
            return Marker("adIcon") == null ? Matches(bitmap, "adOn") : CountAdvertisementPixels(bitmap, true) >= 35;
        }

        private int CountAdvertisementPixels(Bitmap image, bool green)
        {
            VisualMarker icon = Marker("adIcon");
            if (icon == null) return 0;
            Rectangle area = ScreenVision.Base(image, icon);
            int count = 0;
            for (int y = area.Top + 7; y < area.Bottom - 11; y += 2)
                for (int x = area.Left + 8; x < area.Right - 6; x += 2)
                {
                    Color pixel = image.GetPixel(x, y);
                    if (green ? pixel.G > 95 && pixel.G - pixel.R > 19 && pixel.G - pixel.B > 35
                        : pixel.R > 185 && pixel.G > 180 && pixel.B > 155 && pixel.R - pixel.B < 60) count++;
                }
            return count;
        }

        private async Task Reconnect(Bitmap frame)
        {
            if (reconnects >= 2 || !Matches(frame, "reconnect"))
                throw new CycleStop("Потеряно соединение. Дождись восстановления и запусти цикл заново.");
            reconnects++;
            Stage("Переподключаюсь");
            await TapMark("reconnect").ConfigureAwait(false);
            await Task.Delay(1800, stop).ConfigureAwait(false);
        }

        private async Task WaitFor(Func<Bitmap, bool> predicate, string message, int checks = 12, bool handleFull = false)
        {
            for (int check = 0; check < checks; check++)
            {
                using (Bitmap bitmap = Capture())
                {
                    if (handleFull && Matches(bitmap, "full"))
                    {
                        capacityExceeded = await CloseFullSilo().ConfigureAwait(false);
                        continue;
                    }
                    CheckObstacles(bitmap);
                    try { if (predicate(bitmap)) return; }
                    catch (CameraNotFound) { }
                }
                await Task.Delay(260, stop).ConfigureAwait(false);
            }
            throw new CycleStop(message);
        }

        private Plot[] ReadPlots(Bitmap bitmap)
        {
            CheckObstacles(bitmap);
            Point offset = FindOffset(bitmap);
            return Enumerable.Range(0, points.Length).Select(index =>
            {
                Point point = PlotPoint(bitmap, index, offset);
                string state = ReadPlot(bitmap, point);
                if (state == "ripe" && ReadCropColor(bitmap, point) == "seeded") state = "seeded";
                if (calibration.Field != null && (state == null || state == "ripe") &&
                    ReadCropColor(bitmap, new Point(point.X - 8, point.Y + 3)) == "empty" &&
                    ReadCropColor(bitmap, new Point(point.X - 12, point.Y + 1)) == "empty")
                    state = "empty";
                if (state == null && calibration.Field != null)
                    state = ReadPlot(bitmap, new Point(point.X + 8, point.Y - 4)) ??
                        ReadPlot(bitmap, new Point(point.X - 7, point.Y - 1)) ?? ReadCropColor(bitmap, point);
                return new Plot { Index = index, State = state };
            }).ToArray();
        }

        private string ReadCropColor(Bitmap image, Point point)
        {
            int ripe = 0, green = 0, brown = 0, count = 0;
            for (int y = point.Y - 4; y <= point.Y + 3; y++)
                for (int x = point.X - 6; x <= point.X + 5; x++)
                {
                    if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                    Color pixel = image.GetPixel(x, y);
                    count++;
                    if (pixel.R >= 155 && pixel.G >= 120 && pixel.R - pixel.G >= 8 && pixel.R - pixel.G <= 65 &&
                        pixel.G - pixel.B >= 55) ripe++;
                    else if (pixel.G >= 80 && pixel.G - pixel.R >= 12 && pixel.G - pixel.B >= 30) green++;
                    else if (pixel.R - pixel.G >= 24 && pixel.G - pixel.B >= 12) brown++;
                }
            if (count == 0) return null;
            if (ripe * 100 >= count * 53) return "ripe";
            if (green * 100 >= count * 65 || (green * 100 >= count * 19 && brown * 100 >= count * 12)) return "seeded";
            if (brown * 100 >= count * 80) return "empty";
            return null;
        }

        private string ReadPlot(Bitmap image, Point point)
        {
            var grades = new[] { "empty", "seeded", "ripe" }.Select(state => new
            {
                State = state,
                Score = state == "seeded"
                    ? Math.Min(PlotTemplateDistance(image, point, "seeded"), PlotTemplateDistance(image, point, "growing"))
                    : PlotTemplateDistance(image, point, state)
            }).OrderBy(value => value.Score).ToArray();
            return grades[0].Score <= 25 && grades[1].Score - grades[0].Score >= 3 ? grades[0].State : null;
        }

        private double PlotTemplateDistance(Bitmap image, Point point, string key)
        {
            VisualMarker marker = Marker(key);
            return marker == null ? Double.PositiveInfinity :
                ScreenVision.Distance(image, marker, ScaleAroundCenter(ScreenVision.At(image, marker, point)));
        }

        private Point PlotPoint(Bitmap bitmap, int index, Point offset)
        {
            Point point = Transform(points[index], offset);
            if (!new Rectangle(0, 0, bitmap.Width, bitmap.Height).Contains(point))
                throw new CycleStop("Грядка №" + (index + 1) + " вышла из кадра.");
            return point;
        }

        private Point FindOffset(Bitmap bitmap)
        {
            if (!IsFarmScreen(bitmap)) throw new CameraNotFound();
            VisualMarker anchor = Marker("anchor");
            var anchors = new[] { anchor, Marker("anchorRoof"), Marker("anchorSilo") }.Where(marker => marker != null).ToArray();
            cameraOrigin = ScreenVision.Center(ScreenVision.Base(bitmap, anchor));
            VisualMarker stall = Marker("stall");
            Point? current = null;
            double best = 24;
            foreach (VisualMarker referenceMarker in anchors)
                ScoreAnchor(bitmap, referenceMarker, stall, cameraOffset, 18, ref best, ref current);

            if (!current.HasValue)
            {
                double previousScale = cameraScale;
                double bestScale = previousScale;
                foreach (double scale in new[] { previousScale, 1.0, 0.95, 1.05, 0.90, 1.10, 0.85, 1.15, 0.80, 1.20, 0.75, 1.25, 1.30 }.Distinct())
                {
                    cameraScale = scale;
                    double oldBest = best;
                    foreach (VisualMarker referenceMarker in anchors)
                        ScoreAnchor(bitmap, referenceMarker, stall, Point.Empty, Math.Max(bitmap.Width, bitmap.Height),
                            ref best, ref current, Math.Abs(scale - previousScale) * 80);
                    if (stall != null)
                        ScoreAnchor(bitmap, stall, anchor, Point.Empty, Math.Max(bitmap.Width, bitmap.Height),
                            ref best, ref current, Math.Abs(scale - previousScale) * 80);
                    if (best < oldBest) bestScale = scale;
                    if (best < 7) break;
                }
                cameraScale = bestScale;
            }
            if (!current.HasValue) throw new CameraNotFound();
            cameraOffset = current.Value;
            return cameraOffset;
        }

        private void ScoreAnchor(Bitmap image, VisualMarker marker, VisualMarker stall, Point around,
            int radius, ref double best, ref Point? foundOffset, double scalePenalty = 0)
        {
            Rectangle reference = Transform(ScreenVision.Base(image, marker), Point.Empty);
            Rectangle expected = reference;
            expected.Offset(around);
            Rectangle? found = ScreenVision.Locate(image, marker, expected, radius, 36);
            if (!found.HasValue) return;
            Point origin = ScreenVision.Center(reference);
            Point center = ScreenVision.Center(found.Value);
            var delta = new Point(center.X - origin.X, center.Y - origin.Y);
            double score = ScreenVision.Distance(image, marker, found.Value);
            int visiblePlots = 0;
            for (int index = 0; index < points.Length; index++)
                if (ReadPlot(image, Transform(points[index], delta)) != null) visiblePlots++;
            score += (1.0 - (double)visiblePlots / points.Length) * 20 - 7;
            if (stall != null)
            {
                Rectangle shop = Transform(ScreenVision.Base(image, stall), delta);
                double shopScore = ScreenVision.Distance(image, stall, shop);
                if (shopScore <= 26) score = (score * 2 + shopScore) / 3;
                else if (radius > 80) score += 8;
            }
            score += scalePenalty;
            if (score >= best) return;
            best = score;
            foundOffset = delta;
        }

        private sealed class CameraNotFound : Exception
        {
            public CameraNotFound() : base("Камера сместилась или изменился масштаб. Верни ориентир в кадр.") { }
        }

        private Point Transform(Point point, Point offset)
        {
            return new Point(cameraOrigin.X + offset.X + (int)Math.Round((point.X - cameraOrigin.X) * cameraScale),
                cameraOrigin.Y + offset.Y + (int)Math.Round((point.Y - cameraOrigin.Y) * cameraScale));
        }

        private Rectangle Transform(Rectangle area, Point offset)
        {
            Point center = Transform(ScreenVision.Center(area), offset);
            Rectangle scaled = ScaleAroundCenter(area);
            scaled.Location = new Point(center.X - scaled.Width / 2, center.Y - scaled.Height / 2);
            return scaled;
        }

        private Rectangle ScaleAroundCenter(Rectangle area)
        {
            Point center = ScreenVision.Center(area);
            int width = Math.Max(3, (int)Math.Round(area.Width * cameraScale));
            int height = Math.Max(3, (int)Math.Round(area.Height * cameraScale));
            return new Rectangle(center.X - width / 2, center.Y - height / 2, width, height);
        }

        private void CheckObstacles(Bitmap bitmap)
        {
            if (Matches(bitmap, "full")) throw new SiloFull();
            if (Matches(bitmap, "noSeeds")) throw new CycleStop("Недостаточно семян.");
            if (Matches(bitmap, "offline")) throw new CycleStop("Потеряно соединение. Состояние будет считано при новом старте.");
        }

        private sealed class SiloFull : Exception
        {
            public SiloFull() : base("Силос заполнен.") { }
        }

        private Rectangle? LocateTool(Bitmap bitmap, string tool, Point plot)
        {
            var marker = Marker(tool);
            Rectangle expected = ScreenVision.Base(bitmap, marker);
            VisualMarker target = Marker(tool + "Target");
            if (target != null)
            {
                Point previous = ScreenVision.Center(ScreenVision.Base(bitmap, target));
                Point center = ScreenVision.Center(expected);
                expected.Location = new Point(
                    plot.X + (int)Math.Round((center.X - previous.X) * cameraScale) - expected.Width / 2,
                    plot.Y + (int)Math.Round((center.Y - previous.Y) * cameraScale) - expected.Height / 2);
            }
            return ScreenVision.Locate(bitmap, marker, expected, 28, 17)
                ?? ObjectVision.Find(bitmap, marker, expected, 48, stop, tool == "sickle" ? "sickle" : null)
                ?? ScreenVision.Locate(bitmap, marker, expected, 105, 17);
        }

        private bool SlotMatches(Bitmap bitmap, string key, Point point)
        {
            VisualMarker marker = Marker(key);
            return marker != null && ScreenVision.Locate(bitmap, marker, ScreenVision.At(bitmap, marker, point), 3, 18).HasValue;
        }

        private bool IsSold(Bitmap bitmap, Point point)
        {
            VisualMarker heading = Marker("soldHeader");
            VisualMarker origin = Marker("soldHeaderOrigin");
            if (heading != null && origin != null)
            {
                Rectangle expected = ScreenVision.Base(bitmap, heading);
                Point source = ScreenVision.Center(ScreenVision.Base(bitmap, origin));
                expected.Offset(point.X - source.X, point.Y - source.Y);
                if (ScreenVision.Locate(bitmap, heading, expected, 4, 19).HasValue) return true;
            }
            return SlotMatches(bitmap, "sold", point) || SlotMatches(bitmap, "soldAlt", point);
        }

        private void RequireListing(Bitmap bitmap)
        {
            CheckObstacles(bitmap);
            if (!Matches(bitmap, "listing") || !Matches(bitmap, "selected") || !Matches(bitmap, "coins"))
                throw new CycleStop("Форма продажи за монеты изменилась.");
        }

        private VisualMarker Marker(string key)
        {
            VisualMarker found;
            return calibration.Markers.TryGetValue(key, out found) ? found : null;
        }

        private bool Matches(Bitmap bitmap, string key) { return ScreenVision.Matches(bitmap, Marker(key), 17); }

        private bool IsFarmScreen(Bitmap bitmap)
        {
            return Matches(bitmap, "farm") || ScreenVision.Matches(bitmap, Marker("farmEnvelope"), 13);
        }

        private int ReadNumber(Bitmap bitmap, string key, Point offset = default(Point))
        {
            VisualRegion region;
            if (!calibration.Counters.TryGetValue(key, out region)) throw new CycleStop("Область счётчика не настроена.");
            if (offset != Point.Empty)
            {
                Rectangle area = region.ToRectangle(bitmap.Width, bitmap.Height);
                area.Offset(offset);
                region = VisualRegion.FromRectangle(area, bitmap.Width, bitmap.Height);
            }
            return numbers.Read(bitmap, region, stop, key == "amount");
        }

        private async Task TapMark(string key)
        {
            using (Bitmap bitmap = Capture())
            {
                VisualMarker marker = Marker(key);
                if (marker == null) throw new CycleStop("Не настроена кнопка. " + key);
                Rectangle expected = ScreenVision.Base(bitmap, marker);
                Rectangle? button = ScreenVision.Locate(bitmap, marker, expected, 6, 17);
                if (!button.HasValue) throw new CycleStop("Кнопка не найдена. " + key);
                await Tap(ScreenVision.Center(button.Value)).ConfigureAwait(false);
            }
        }

        private async Task Tap(Point point)
        {
            await surface.TapAsync(calibration.Viewport, point, stop).ConfigureAwait(false);
            await Task.Delay(290, stop).ConfigureAwait(false);
        }

        private Bitmap Capture()
        {
            stop.ThrowIfCancellationRequested();
            Bitmap bitmap;
            bool paused = false;
            while (true)
            {
                if (surface.IsForeground())
                {
                    try { bitmap = surface.Capture(calibration.Viewport); break; }
                    catch (CycleStop) { if (surface.IsForeground()) throw; }
                }
                surface.ClientBounds();
                if (!paused) report("Жду возврата в BlueStacks");
                paused = true;
                if (stop.WaitHandle.WaitOne(250)) stop.ThrowIfCancellationRequested();
            }
            if (paused) report(currentStage);
            if (lastScreen != null) lastScreen.Dispose();
            lastScreen = (Bitmap)bitmap.Clone();
            return bitmap;
        }

        private void Stage(string text)
        {
            stop.ThrowIfCancellationRequested();
            currentStage = text;
            report(text);
        }

        public void Dispose()
        {
            if (lastScreen != null) lastScreen.Dispose();
        }

        private sealed class Plot
        {
            public int Index;
            public string State;
        }
    }
}
