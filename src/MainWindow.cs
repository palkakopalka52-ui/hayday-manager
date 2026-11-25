using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WorkBitmap = System.Drawing.Bitmap;

namespace FarmDesk
{
    internal sealed class MainWindow : Window
    {
        private const int StopKey = 0x78, StopHotkeyId = 0x6a34, WmHotkey = 0x0312;
        private readonly BlueStacksService blue;
        private readonly DataStore store;
        private readonly ManagerSettings state;
        private readonly StackPanel cardList;
        private readonly TextBlock headerDetails, name, hardware, operation, detail, metric, calibrationInfo;
        private readonly TextBox comment, batch, price, reserve, wait;
        private readonly CheckBox advertise, useTestFarm, repeat;
        private readonly ComboBox windowCap;
        private readonly Button attach, start, resume, startAll, stopButton, failureShot, calibrate, openAndroid, openGame, inspect, close;
        private readonly DispatcherTimer poll;
        private List<DeviceInfo> known = new List<DeviceInfo>();
        private List<PlayerInstance> players = new List<PlayerInstance>();
        private FarmCard selection;
        private readonly Dictionary<string, DateTime> advertisementCooldowns = new Dictionary<string, DateTime>();
        private CancellationTokenSource running;
        private HwndSource source;
        private bool refreshing, loadingEditor, hotkey, closeAfterStop;
        private int lastMetricPid;
        private DateTime lastMetricTime;
        private TimeSpan lastMetricCpu;

        public MainWindow(BlueStacksService blue, DataStore store, ManagerSettings state)
        {
            this.blue = blue; this.store = store; this.state = state;
            Title = "Hay Day · Фермы";
            Width = 1050; Height = 685; MinWidth = 880; MinHeight = 475;
            Background = Theme.Canvas;
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = root;

            var header = new Grid { Background = Theme.Dark };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Margin = new Thickness(24, 14, 12, 16) };
            title.Children.Add(Theme.Text("Фермы", 32, Theme.Paper, true));
            headerDetails = Theme.Text("BlueStacks 5", 13, Theme.Brush("#B9C6BA"));
            title.Children.Add(headerDetails);
            header.Children.Add(title);
            var mainActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 17, 0) };
            Grid.SetColumn(mainActions, 1);
            startAll = Theme.Button("Запустить все", Theme.Green, Theme.Paper, async delegate { await RunQueue(true); });
            stopButton = Theme.Button("Стоп · F9", Theme.Red, Theme.Paper, delegate { Halt(); });
            mainActions.Children.Add(startAll);
            mainActions.Children.Add(stopButton);
            header.Children.Add(mainActions);
            root.Children.Add(header);

            var body = new Grid { Margin = new Thickness(21, 18, 21, 18) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(273) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            var left = new DockPanel();
            var leftTop = new StackPanel();
            DockPanel.SetDock(leftTop, Dock.Top);
            leftTop.Children.Add(Theme.Text("Устройства", 21, Theme.Ink, true));
            leftTop.Children.Add(Theme.Gap(10));
            var inventoryButtons = new StackPanel { Orientation = Orientation.Horizontal };
            inventoryButtons.Children.Add(Theme.Button("Создать", Theme.Shade, Theme.Ink, delegate { Do(blue.OpenManager); }));
            attach = Theme.Button("Подключить", Theme.Shade, Theme.Ink, delegate { ShowAttachMenu(); });
            inventoryButtons.Children.Add(attach);
            leftTop.Children.Add(inventoryButtons);
            leftTop.Children.Add(Theme.Gap(10));
            left.Children.Add(leftTop);

            var queueSettings = new StackPanel();
            DockPanel.SetDock(queueSettings, Dock.Bottom);
            queueSettings.Children.Add(Theme.Gap(12));
            queueSettings.Children.Add(Theme.Text("Лимит окон Android", 13, Theme.Soft, true));
            windowCap = new ComboBox
            {
                ItemsSource = Enumerable.Range(1, 8).ToArray(),
                SelectedItem = state.ParallelLimit, Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(8)
            };
            windowCap.SelectionChanged += delegate
            {
                if (windowCap.SelectedItem == null) return;
                state.ParallelLimit = (int)windowCap.SelectedItem;
                Save();
            };
            queueSettings.Children.Add(windowCap);
            left.Children.Add(queueSettings);
            cardList = new StackPanel();
            left.Children.Add(new ScrollViewer { Content = cardList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(Theme.Card(left, Theme.Paper, 15, 15));

            var main = new StackPanel();
            var scroll = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 2);
            body.Children.Add(scroll);
            var top = new StackPanel();
            name = Theme.Text("Выбери устройство", 26, Theme.Ink, true);
            hardware = Theme.Text("", 13, Theme.Soft);
            top.Children.Add(name);
            top.Children.Add(hardware);
            top.Children.Add(Theme.Gap(9));
            var playerActions = new WrapPanel();
            openAndroid = Theme.Button("Открыть Android", Theme.Shade, Theme.Ink, delegate
            {
                Do(delegate
                {
                    FarmCard card = RequireCard();
                    CheckCapacity(card.InstanceId);
                    using (Process process = blue.Start(card.InstanceId)) { }
                    SetStage(card, "Запуск Android", "");
                });
            });
            openGame = Theme.Button("Открыть Hay Day", Theme.Shade, Theme.Ink, delegate
            {
                Do(delegate
                {
                    FarmCard card = RequireCard();
                    CheckCapacity(card.InstanceId);
                    blue.OpenGame(card.InstanceId);
                    SetStage(card, "Запуск Hay Day", "");
                });
            });
            inspect = Theme.Button("Проверить", Theme.Shade, Theme.Ink, async delegate { await Inspect(); });
            close = Theme.Button("Закрыть окно", Theme.Shade, Theme.Ink, delegate
            {
                Do(delegate
                {
                    FarmCard card = RequireCard();
                    if (!blue.RequestClose(card.InstanceId)) throw new CycleStop("Окно устройства закрыто или ещё загружается.");
                    SetStage(card, "Закрытие Android", "");
                });
            });
            foreach (Button button in new[] { openGame, close, openAndroid, inspect }) playerActions.Children.Add(button);
            top.Children.Add(playerActions);
            main.Children.Add(Theme.Card(top, Theme.Paper, 15, 15));
            main.Children.Add(Theme.Gap(12));

            var panel = new StackPanel();
            operation = Theme.Text("Готово", 22, Theme.Ink, true);
            detail = Theme.Text("", 13, Theme.Soft);
            detail.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(operation);
            panel.Children.Add(detail);
            var runButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            calibrate = Theme.Button("Калибровка", Theme.Shade, Theme.Ink, delegate { Do(OpenCalibration); });
            start = Theme.Button("Старт с пустого поля", Theme.Green, Theme.Paper, async delegate { await RunQueue(false); });
            resume = Theme.Button("Продолжить посевы", Theme.Shade, Theme.Ink, async delegate { await RunQueue(false, true); });
            foreach (Button button in new[] { start, resume, calibrate }) runButtons.Children.Add(button);
            panel.Children.Add(runButtons);
            var infoRow = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var notes = new StackPanel();
            metric = Theme.Text("Нагрузка окна · —", 12, Theme.Soft);
            calibrationInfo = Theme.Text("", 12, Theme.Soft);
            notes.Children.Add(metric); notes.Children.Add(calibrationInfo);
            infoRow.Children.Add(notes);
            failureShot = Theme.Button("Снимок сбоя", Theme.RedLight, Theme.Red, delegate
            {
                Do(delegate
                {
                    string path = RequireCard().LastScreenshot;
                    if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new CycleStop("Снимок недоступен.");
                    using (Process process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                });
            });
            Grid.SetColumn(failureShot, 1);
            infoRow.Children.Add(failureShot);
            panel.Children.Add(infoRow);
            main.Children.Add(Theme.Card(panel, Theme.Paper, 15, 15));
            main.Children.Add(Theme.Gap(12));

            var preferences = new StackPanel();
            preferences.Children.Add(Theme.Text("Настройки карточки", 17, Theme.Ink, true));
            preferences.Children.Add(Theme.Gap(7));
            comment = new TextBox { MinHeight = 30, Padding = new Thickness(6), BorderBrush = Theme.Line };
            var commentLabel = Theme.Text("Комментарий", 12, Theme.Soft);
            preferences.Children.Add(commentLabel);
            preferences.Children.Add(comment);
            var fields = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            batch = Numeric(fields, "Размер партии");
            price = Numeric(fields, "Цена партии, мон.");
            reserve = Numeric(fields, "Запас до сбора");
            wait = Numeric(fields, "Проверка созревания, с");
            preferences.Children.Add(fields);
            var checks = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            advertise = new CheckBox { Content = "Бесплатное объявление", Margin = new Thickness(0, 0, 17, 0) };
            useTestFarm = new CheckBox { Content = "Отдельная тестовая ферма", Margin = new Thickness(0, 0, 17, 0) };
            repeat = new CheckBox { Content = "Повторять до остановки" };
            checks.Children.Add(advertise); checks.Children.Add(useTestFarm); checks.Children.Add(repeat);
            preferences.Children.Add(checks);
            advertise.Click += delegate { SaveEditorQuietly(); };
            useTestFarm.Click += delegate { SaveEditorQuietly(); UpdateButtons(); };
            repeat.Click += delegate { SaveEditorQuietly(); };
            foreach (TextBox textbox in new[] { comment, batch, price, reserve, wait })
                textbox.LostKeyboardFocus += delegate { SaveEditorQuietly(); };
            main.Children.Add(Theme.Card(preferences, Theme.Paper, 15, 15));

            SourceInitialized += delegate
            {
                source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook(HotkeyHook);
                hotkey = RegisterHotKey(source.Handle, StopHotkeyId, 0x4000, StopKey);
                if (!hotkey) stopButton.Content = "Стоп";
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (running != null) { args.Cancel = true; closeAfterStop = true; Halt(); return; }
                SaveEditorQuietly();
                if (source != null && hotkey) UnregisterHotKey(source.Handle, StopHotkeyId);
                if (poll != null) poll.Stop();
            };
            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            poll.Tick += async delegate { await RefreshEnvironment(); };
            Loaded += async delegate { await RefreshEnvironment(); poll.Start(); };
            Select(state.Cards.FirstOrDefault());
        }

        private static TextBox Numeric(Panel target, string label)
        {
            var group = new StackPanel { MinWidth = 92, Margin = new Thickness(0, 0, 12, 0) };
            group.Children.Add(Theme.Text(label, 12, Theme.Soft));
            var box = new TextBox { Width = 74, MinHeight = 27, Padding = new Thickness(5, 3, 5, 3), BorderBrush = Theme.Line, HorizontalAlignment = HorizontalAlignment.Left };
            group.Children.Add(box);
            target.Children.Add(group);
            return box;
        }

        private async Task RefreshEnvironment()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var probe = await Task.Run(() => Tuple.Create(blue.ReadDevices(), blue.ReadPlayers()));
                known = probe.Item1;
                players = probe.Item2;
                headerDetails.Text = String.IsNullOrEmpty(blue.Version) ? "BlueStacks не обнаружен"
                    : String.Format("BlueStacks {0} · окон Android {1}", blue.Version, players.Count(player => player.Handle != IntPtr.Zero));
                DeviceInfo initialDevice = known.FirstOrDefault(device => device.Id == "Pie64")
                    ?? (known.Count == 1 ? known[0] : null);
                if (state.Cards.Count == 0 && initialDevice != null)
                {
                    FarmCard card = state.Attach(initialDevice.Id);
                    Save();
                    Select(card);
                }
                DrawCards();
                ShowCardMetadata();
                SampleSelected();
                UpdateButtons();
            }
            catch (Exception error) { detail.Text = error.Message; }
            finally { refreshing = false; }
        }

        private void DrawCards()
        {
            cardList.Children.Clear();
            if (state.Cards.Count == 0)
            {
                cardList.Children.Add(Theme.Text("Подключи экземпляр BlueStacks.", 13, Theme.Soft));
                return;
            }
            foreach (FarmCard card in state.Cards)
            {
                bool selected = ReferenceEquals(card, selection);
                var label = new StackPanel { MinWidth = 191 };
                label.Children.Add(Theme.Text(String.Format("#{0:00}  {1}", card.Number, card.InstanceId), 15, Theme.Ink, true));
                var shortText = Theme.Text(String.IsNullOrWhiteSpace(card.Comment) ? Status(card) : card.Comment, 12, Theme.Soft);
                shortText.TextTrimming = TextTrimming.CharacterEllipsis;
                shortText.TextWrapping = TextWrapping.NoWrap;
                label.Children.Add(shortText);
                var badge = Theme.Text(Status(card) + " · " + (card.LastOperation ?? ""), 12, StatusColor(card), true);
                badge.TextTrimming = TextTrimming.CharacterEllipsis;
                badge.TextWrapping = TextWrapping.NoWrap;
                label.Children.Add(badge);
                FarmCard target = card;
                var button = Theme.Button("", selected ? Theme.GreenLight : Theme.Canvas, Theme.Ink, delegate
                {
                    SaveEditorQuietly();
                    Select(target);
                });
                button.Content = label;
                button.Margin = new Thickness(0, 0, 0, 8);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                cardList.Children.Add(button);
            }
        }

        private Brush StatusColor(FarmCard card)
        {
            if ((card.LastOperation ?? "").StartsWith("Ошибка", StringComparison.Ordinal)
                || card.LastOperation == "Остановлено") return Theme.Red;
            return Player(card.InstanceId) != null ? Theme.Green : Theme.Soft;
        }

        private string Status(FarmCard card)
        {
            if (!known.Any(device => device.Id == card.InstanceId)) return "Устройство не найдено";
            PlayerInstance player = Player(card.InstanceId);
            if (player == null) return "Закрыто";
            return player.Handle != IntPtr.Zero ? "Окно открыто" : "Загрузка Android";
        }

        private void Select(FarmCard card)
        {
            selection = card;
            loadingEditor = true;
            try
            {
                comment.Text = card == null ? "" : card.Comment ?? "";
                FarmOptions opts = card == null ? new FarmOptions() : card.Options;
                batch.Text = opts.Batch.ToString(CultureInfo.InvariantCulture);
                price.Text = opts.Price.ToString(CultureInfo.InvariantCulture);
                reserve.Text = opts.Reserve.ToString(CultureInfo.InvariantCulture);
                wait.Text = opts.WaitIntervalSeconds.ToString(CultureInfo.InvariantCulture);
                advertise.IsChecked = opts.FreeAdvertisement;
                useTestFarm.IsChecked = card != null && card.IsTestFarm;
                repeat.IsChecked = card != null && card.RepeatUntilStopped;
            }
            finally { loadingEditor = false; }
            ShowCardMetadata();
            ShowOperation();
            DrawCards();
            UpdateButtons();
        }

        private void ShowCardMetadata()
        {
            if (selection == null)
            {
                name.Text = "Выбери устройство"; hardware.Text = ""; calibrationInfo.Text = "";
                return;
            }
            DeviceInfo device = known.FirstOrDefault(item => item.Id == selection.InstanceId);
            name.Text = String.Format("#{0:00}  {1}", selection.Number, device == null ? selection.InstanceId : device.Id);
            hardware.Text = device == null ? Status(selection) : String.Format("{0} · {1}×{2} · {3} FPS · {4} vCPU · {5} МиБ · создана {6:dd.MM.yyyy}",
                Status(selection), device.Width, device.Height, device.Fps, device.Cores, device.RamMiB, selection.CreatedLocal);
            int missing = ReadCalibration(selection).Missing(false).Length;
            calibrationInfo.Text = missing == 0 ? "Калибровка готова" : "Калибровка · осталось " + missing + " элементов";
        }

        private void ShowOperation()
        {
            operation.Text = selection == null ? "Выбери карточку" : selection.LastOperation ?? "Готово";
            detail.Text = selection == null ? "" : selection.LastDetail ?? "";
            operation.Foreground = operation.Text == "Остановлено" || operation.Text.StartsWith("Ошибка", StringComparison.Ordinal)
                ? Theme.Red : running != null || operation.Text == "Цикл завершён" || operation.Text == LiveCycle.FieldComplete
                    || operation.Text == LiveCycle.HarvestComplete ? Theme.Green : Theme.Ink;
        }

        private void SampleSelected()
        {
            PlayerInstance player = selection == null ? null : Player(selection.InstanceId);
            if (player == null) { metric.Text = "Нагрузка окна · —"; lastMetricPid = 0; return; }
            try
            {
                using (Process process = Process.GetProcessById(player.ProcessId))
                {
                    var now = DateTime.UtcNow;
                    TimeSpan cpu = process.TotalProcessorTime;
                    string percent = lastMetricPid != player.ProcessId ? "—" : String.Format(CultureInfo.GetCultureInfo("ru-RU"), "{0:0.0}%",
                        100 * (cpu - lastMetricCpu).TotalSeconds / Math.Max(0.01, (now - lastMetricTime).TotalSeconds) / Environment.ProcessorCount);
                    metric.Text = String.Format("HD-Player · CPU {0} · рабочий набор {1:N0} МиБ", percent, process.WorkingSet64 / 1048576.0);
                    lastMetricPid = player.ProcessId; lastMetricTime = now; lastMetricCpu = cpu;
                }
            }
            catch { metric.Text = "Нагрузка окна недоступна"; }
        }

        private void UpdateButtons()
        {
            bool idle = running == null;
            bool exists = selection != null;
            bool player = exists && Player(selection.InstanceId) != null;
            start.IsEnabled = resume.IsEnabled = idle && exists && selection.IsTestFarm;
            startAll.IsEnabled = idle && state.Cards.Any(card => card.IsTestFarm);
            stopButton.IsEnabled = !idle;
            windowCap.IsEnabled = idle;
            batch.IsEnabled = price.IsEnabled = reserve.IsEnabled = wait.IsEnabled = advertise.IsEnabled = idle && exists;
            calibrate.IsEnabled = idle && exists;
            openAndroid.IsEnabled = idle && exists;
            openGame.IsEnabled = idle && exists;
            close.IsEnabled = idle && player;
            inspect.IsEnabled = idle && player;
            useTestFarm.IsEnabled = idle && exists;
            repeat.IsEnabled = idle && exists;
            failureShot.Visibility = exists && !String.IsNullOrEmpty(selection.LastScreenshot)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowAttachMenu()
        {
            var menu = new ContextMenu { PlacementTarget = attach };
            foreach (DeviceInfo device in known)
            {
                DeviceInfo target = device;
                var item = new MenuItem { Header = String.Format("{0} · {1}", device.Id, device.Name) };
                item.Click += delegate { Do(delegate { SaveEditorQuietly(); Select(state.Attach(target.Id)); Save(); }); };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Экземпляров нет", IsEnabled = false });
            menu.IsOpen = true;
        }

        private void OpenCalibration()
        {
            FarmCard card = RequireCard();
            new CalibrationWindow(this, store, card.InstanceId,
                () => blue.ReadPlayers().FirstOrDefault(player => player.InstanceId == card.InstanceId)).ShowDialog();
            ShowCardMetadata();
        }

        private FarmCalibration ReadCalibration(FarmCard card)
        {
            var profile = store.Read<FarmCalibration>(Path.Combine(store.DeviceFolder(card.InstanceId), "calibration.json"))
                ?? new FarmCalibration();
            profile.Normalize();
            return profile;
        }

        private async Task Inspect()
        {
            FarmCard card = selection;
            if (card == null) return;
            try
            {
                SetStage(card, "Проверка установки", "");
                string response = await Task.Run(() => blue.Inspect(card.InstanceId, CancellationToken.None));
                SetStage(card, "Установка проверена", response);
            }
            catch (Exception error) { SetStage(card, "Ошибка проверки", error.Message); }
        }

        private async Task RunQueue(bool all, bool resumeCrops = false)
        {
            if (running != null || selection == null) return;
            try { SaveEditor(); }
            catch (Exception error) { Do(delegate { throw new CycleStop(error.Message); }); return; }
            FarmCard[] jobs = (all ? state.Cards : new List<FarmCard> { selection }).Where(card => card.IsTestFarm).ToArray();
            if (jobs.Length == 0) return;
            var pending = new Queue<FarmCard>(jobs);
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            FarmCard active = null;
            running = new CancellationTokenSource();
            CancellationToken token = running.Token;
            try
            {
                UpdateButtons();
                foreach (FarmCard card in jobs) SetStage(card, "В очереди", "");
                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    active = pending.Dequeue();
                    Select(active);
                    bool first = attempted.Add(active.InstanceId);
                    await RunLive(active, token, first && !resumeCrops, first && resumeCrops);

                    bool shouldRepeat = active.RepeatUntilStopped;
                    if (shouldRepeat) pending.Enqueue(active);
                    if (pending.Count != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        FarmCard next = pending.Peek();
                        if (!ReferenceEquals(active, next))
                        {
                            if (WantsSequential(next.InstanceId)) await CloseForNext(active, token);
                            if (shouldRepeat) SetStage(active, "В очереди", active.LastDetail);
                        }
                    }
                    active = null;
                }
            }
            catch (OperationCanceledException)
            {
                if (active != null) SetStage(active, "Остановлено", "");
                foreach (FarmCard card in pending.Where(card => !ReferenceEquals(card, active)))
                    SetStage(card, "Остановлено", "");
            }
            catch (Exception error)
            {
                if (active != null) SetStage(active, "Ошибка цикла", error.Message);
                else detail.Text = error.Message;
                foreach (FarmCard card in pending.Where(card => !ReferenceEquals(card, active)))
                    SetStage(card, "Ожидает запуска", "Предыдущий цикл остановился.");
            }
            finally
            {
                running.Dispose();
                running = null;
                UpdateButtons();
                ShowOperation();
                if (closeAfterStop) Close();
            }
        }

        private async Task RunLive(FarmCard card, CancellationToken token, bool requireEmptyStart, bool sellPlantedOnResume)
        {
            FarmCalibration profile = ReadCalibration(card);
            string[] missing = profile.Missing(false);
            if (missing.Length != 0) throw new CycleStop("Нужна калибровка · " + String.Join(", ", missing.Take(6)) + ".");
            if (card.RepeatUntilStopped && !profile.Markers.ContainsKey("shopClose"))
                throw new CycleStop("Для повторения отметь кнопку закрытия киоска в калибровке.");
            FarmOptions options = card.Options.Copy();
            LiveCycle.ValidateOptions(options, profile.PlotPoints().Length, false);
            CheckCapacity(card.InstanceId);
            SetStage(card, "Открываю ферму", "");
            PlayerInstance player = await Task.Run(() => blue.ReadPlayers()
                .FirstOrDefault(item => item.InstanceId == card.InstanceId && item.Handle != IntPtr.Zero), token);
            if (player == null)
            {
                blue.OpenGame(card.InstanceId);
                player = await WaitForPlayer(card.InstanceId, token);
            }
            var surface = new DesktopSurface(player);
            await surface.ActivateAsync(token);
            VisualRegion viewport = await Task.Run(() => surface.DetectGameViewport(profile), token);
            if (viewport == null) throw new CycleStop("Не найдена игровая область BlueStacks. Покажи экран фермы целиком.");
            profile.Viewport = viewport;
            var ocr = new NumberReader();
            DateTime nextAdvertisement;
            advertisementCooldowns.TryGetValue(card.InstanceId, out nextAdvertisement);
            using (var cycle = new LiveCycle(surface, profile, options, ocr, token, operation =>
                PostStage(card, operation), false, nextAdvertisement))
            {
                try
                {
                    CycleResult result = await Task.Run(() => cycle.RunAsync(requireEmptyStart, sellPlantedOnResume), token);
                    string summary = String.Format(CultureInfo.GetCultureInfo("ru-RU"),
                            "Посев {0} · сбор {1} · пересев {2} · выставлено {3} шт. · партий {4} по {5} мон. · остаток {6} · {7:0.0} с",
                            result.Sown, result.Harvested, result.Replanted, result.Listed, result.Listings, options.Price,
                            result.RemainingStock >= 0 ? result.RemainingStock.ToString(CultureInfo.InvariantCulture) : "—",
                            result.Duration.TotalSeconds)
                            + (result.CollectedSales == 0 ? "" : " · получены монеты из ячеек " + result.CollectedSales)
                            + (result.Advertised ? " · бесплатное объявление" : "");
                    SetStage(card, result.FinalStatus, summary);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    using (WorkBitmap last = cycle.LastScreen()) if (last != null) KeepFailure(card, last);
                    throw;
                }
                finally { advertisementCooldowns[card.InstanceId] = cycle.NextAdvertisementUtc; }
            }
        }

        private async Task<PlayerInstance> WaitForPlayer(string instance, CancellationToken token)
        {
            for (int attempt = 0; attempt < 70; attempt++)
            {
                token.ThrowIfCancellationRequested();
                PlayerInstance player = await Task.Run(() => blue.ReadPlayers()
                    .FirstOrDefault(item => item.InstanceId == instance && item.Handle != IntPtr.Zero));
                if (player != null) return player;
                await Task.Delay(550, token);
            }
            throw new CycleStop("BlueStacks не открыл окно выбранного экземпляра.");
        }

        private async Task CloseForNext(FarmCard card, CancellationToken token)
        {
            string finalStatus = card.LastOperation, finalDetail = card.LastDetail;
            SetStage(card, "Закрываю Android для следующей фермы", finalDetail);
            if (!blue.RequestClose(card.InstanceId)) { SetStage(card, finalStatus, finalDetail); return; }
            for (int attempt = 0; attempt < 240; attempt++)
            {
                await Task.Delay(500, token);
                bool exists = await Task.Run(() => blue.ReadPlayers().Any(player => player.InstanceId == card.InstanceId));
                if (!exists) { SetStage(card, finalStatus, finalDetail); return; }
                if (attempt == 15) SetStage(card, "Ожидаю закрытия Android",
                    "Если BlueStacks показал диалог, подтверди штатное закрытие. Очередь продолжится автоматически.");
            }
            throw new CycleStop("BlueStacks ждёт штатного закрытия. Заверши закрытие окна и запусти очередь повторно.");
        }

        private void KeepFailure(FarmCard card, WorkBitmap frame)
        {
            card.LastScreenshot = store.SaveScreenshot(card.InstanceId, frame);
        }

        private void PostStage(FarmCard card, string stateText)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
            {
                if (running == null || running.IsCancellationRequested) return;
                SetStage(card, stateText, "");
            });
        }

        private void Halt()
        {
            if (running != null) running.Cancel();
        }

        private IntPtr HotkeyHook(IntPtr window, int message, IntPtr code, IntPtr other, ref bool handled)
        {
            if (message == WmHotkey && code.ToInt32() == StopHotkeyId) { Halt(); handled = true; }
            return IntPtr.Zero;
        }

        private bool WantsSequential(string next)
        {
            List<PlayerInstance> current = blue.ReadPlayers();
            if (current.Any(player => player.InstanceId == next)) return false;
            DeviceInfo device = known.FirstOrDefault(item => item.Id == next);
            return current.Count >= state.ParallelLimit || (current.Count > 0 && device != null && FreeMegabytes() < RequiredMegabytes(device));
        }

        private void CheckCapacity(string id)
        {
            List<PlayerInstance> current = blue.ReadPlayers();
            if (current.Any(player => player.InstanceId == id)) return;
            if (current.Count >= state.ParallelLimit) throw new CycleStop("Достигнут лимит окон Android. Закрой открытый экземпляр или измени лимит.");
            DeviceInfo device = known.FirstOrDefault(item => item.Id == id);
            if (current.Count > 0 && device != null && FreeMegabytes() < RequiredMegabytes(device))
                throw new CycleStop("Для второго окна сейчас мало свободной памяти.");
        }

        private PlayerInstance Player(string id) { return players.FirstOrDefault(player => player.InstanceId == id); }

        private FarmCard RequireCard()
        {
            if (selection == null) throw new CycleStop("Выбери карточку устройства.");
            return selection;
        }

        private void SetStage(FarmCard device, string value, string more)
        {
            device.LastOperation = value;
            device.LastDetail = more ?? "";
            device.LastUpdateUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Save();
            DrawCards();
            if (ReferenceEquals(device, selection)) ShowOperation();
        }

        private void SaveEditor()
        {
            if (loadingEditor || selection == null) return;
            FarmOptions options = new FarmOptions
            {
                Batch = Parse(batch.Text, "Размер партии"), Price = Parse(price.Text, "Цена"),
                Reserve = Parse(reserve.Text, "Запас"),
                FreeAdvertisement = advertise.IsChecked == true
            };
            options.WaitIntervalSeconds = Parse(wait.Text, "Интервал");
            LiveCycle.ValidateOptions(options, 1, false);
            selection.IsTestFarm = useTestFarm.IsChecked == true;
            selection.RepeatUntilStopped = repeat.IsChecked == true;
            selection.Options = options;
            selection.Comment = (comment.Text ?? "").Trim();
            Save();
        }

        private void SaveEditorQuietly()
        {
            try { SaveEditor(); }
            catch (Exception error) { detail.Text = error.Message; }
        }

        private static int Parse(string text, string label)
        {
            int value;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new CycleStop(label + " должен быть указан числом.");
            return value;
        }

        private void Save()
        {
            store.Save(state);
        }

        private void Do(Action operation)
        {
            try { operation(); }
            catch (Exception error)
            {
                if (selection != null) SetStage(selection, "Ошибка", error.Message);
                else detail.Text = error.Message;
            }
        }

        private static ulong FreeMegabytes()
        {
            var memory = new MemoryInfo { Length = (uint)Marshal.SizeOf(typeof(MemoryInfo)) };
            return GlobalMemoryStatusEx(ref memory) ? memory.Available / 1048576 : 0;
        }

        private static ulong RequiredMegabytes(DeviceInfo device) { return (ulong)Math.Max(0, device.RamMiB) + 1536UL; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo
        {
            public uint Length, Load;
            public ulong Total, Available, Pagefile, AvailablePagefile, Virtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryInfo info);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
