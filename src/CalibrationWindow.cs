using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WpfPoint = System.Windows.Point;
using PixelRect = System.Drawing.Rectangle;
using PixelPoint = System.Drawing.Point;

namespace FarmDesk
{
    internal sealed class CalibrationWindow : Window
    {
        private const int CenterBoxSize = 11;
        private const int GripBoxSize = 19;
        private readonly DataStore store;
        private readonly Func<PlayerInstance> player;
        private readonly string path;
        private readonly FarmCalibration calibration;
        private readonly ComboBox role;
        private readonly StackPanel areaSettings;
        private readonly TextBox areaStep;
        private readonly List<VisualRegion> areaDraft = new List<VisualRegion>();
        private PixelPoint? areaOffset;
        private readonly TextBlock instructions, status;
        private readonly Canvas canvas, savedPoints;
        private readonly System.Windows.Controls.Image preview;
        private readonly System.Windows.Shapes.Rectangle outline;
        private Bitmap frame;
        private bool raw, selecting, working;
        private WpfPoint origin;

        public CalibrationWindow(Window owner, DataStore store, string instance, Func<PlayerInstance> player)
        {
            this.store = store;
            this.player = player;
            path = Path.Combine(store.DeviceFolder(instance), "calibration.json");
            calibration = store.Read<FarmCalibration>(path) ?? new FarmCalibration();
            calibration.Normalize();
            Owner = owner;
            Title = "Калибровка · " + instance;
            Width = 1050;
            Height = 740;
            MinWidth = 720;
            MinHeight = 525;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Canvas;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

            var root = new DockPanel { Margin = new Thickness(19) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            header.Children.Add(Theme.Text("Калибровка", 29, Theme.Ink, true));
            header.Children.Add(Theme.Gap(8));
            var commands = new WrapPanel();
            header.Children.Add(commands);
            role = new ComboBox { ItemsSource = CalibrationCatalog.Items, MinWidth = 280, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0) };
            role.SelectionChanged += delegate { SetRole(); };
            commands.Children.Add(role);
            commands.Children.Add(Theme.Button("Снять окно Android", Theme.Green, Theme.Paper, async delegate { await Capture(); }));
            commands.Children.Add(Theme.Button("Открыть снимок", Theme.Shade, Theme.Ink, delegate { OpenShot(); }));
            commands.Children.Add(Theme.Button("Убрать последнюю точку", Theme.Shade, Theme.Ink, delegate { RemoveLast(); }));
            areaSettings = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            areaSettings.Children.Add(Theme.Text("Шаг, пикс. ", 13, Theme.Ink));
            areaStep = new TextBox { Text = (calibration.Field == null ? 26 : calibration.Field.Step).ToString(), Width = 36, MaxLength = 2 };
            areaStep.KeyDown += delegate(object _, KeyEventArgs key) { if (key.Key == Key.Enter) SaveStep(); };
            areaSettings.Children.Add(areaStep);
            commands.Children.Add(areaSettings);
            instructions = Theme.Text("", 13, Theme.Soft);
            instructions.Margin = new Thickness(0, 9, 0, 0);
            header.Children.Add(instructions);
            status = Theme.Text("", 13, Theme.Green, true);
            status.Margin = new Thickness(0, 9, 0, 0);
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);

            var host = new Grid { Background = Theme.Dark, ClipToBounds = true };
            preview = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            host.Children.Add(preview);
            savedPoints = new Canvas { IsHitTestVisible = false };
            host.Children.Add(savedPoints);
            canvas = new Canvas { Background = System.Windows.Media.Brushes.Transparent };
            canvas.MouseLeftButtonDown += BeginSelection;
            canvas.MouseMove += UpdateSelection;
            canvas.MouseLeftButtonUp += FinishSelection;
            canvas.SizeChanged += delegate { ShowSavedPoints(); };
            host.Children.Add(canvas);
            outline = new System.Windows.Shapes.Rectangle
            {
                StrokeThickness = 2, Stroke = Theme.Brush("#5FE479"), Fill = Theme.Brush("#334CE268"),
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            canvas.Children.Add(outline);
            root.Children.Add(host);
            Content = root;
            role.SelectedIndex = calibration.Viewport == null ? 0 : 1;
            Closed += delegate
            {
                if (frame != null) frame.Dispose();
                frame = null;
            };
        }

        private MarkerItem Current { get { return (MarkerItem)role.SelectedItem; } }

        private void SetRole()
        {
            if (Current == null || instructions == null) return;
            instructions.Text = SelectionHint(Current);
            areaSettings.Visibility = Current.Kind == MarkerKind.Field ? Visibility.Visible : Visibility.Collapsed;
            outline.Visibility = Visibility.Collapsed;
            ShowSavedPoints();
            SetStatus("");
        }

        private static bool IsToolGrip(MarkerItem item)
        {
            return item.Kind == MarkerKind.Marker && (item.Key == "seed" || item.Key == "sickle");
        }

        private static bool IsPointRole(MarkerItem item)
        {
            return item.Kind == MarkerKind.Field || item.Kind == MarkerKind.Plot || item.Kind == MarkerKind.Slot || IsToolGrip(item)
                || item.Key == "seedTarget" || item.Key == "sickleTarget";
        }

        private static string SelectionHint(MarkerItem item)
        {
            if (item.Kind == MarkerKind.Plot) return "Кликни по центру каждой грядки в порядке прохода. Номера покажут порядок жеста.";
            if (item.Kind == MarkerKind.Slot) return "Кликни по центру каждого слота. Номера покажут порядок проверки.";
            if (IsToolGrip(item)) return "Кликни по центру иконки, за который её можно перетащить. Повторный клик переставит точку.";
            return item.Hint;
        }

        private void SetStatus(string message)
        {
            string[] missing = calibration.Missing();
            status.Text = message.Length == 0
                ? String.Format("Грядок {0} · слотов {1} · обязательных элементов осталось {2}",
                    calibration.PlotPoints().Length, calibration.Slots.Count, missing.Length)
                : message;
        }

        private async Task Capture()
        {
            MarkerItem item = Current;
            if (working || item == null) return;
            working = true;
            bool full = item.Kind == MarkerKind.Viewport;
            try
            {
                if (!full && calibration.Viewport == null) throw new CycleStop("Сначала разметь область Android.");
                var surface = new DesktopSurface(player());
                await surface.ActivateAsync(CancellationToken.None);
                await Task.Delay(380);
                Bitmap taken = full ? surface.CaptureClient() : surface.Capture(calibration.Viewport);
                SetFrame(taken, full);
                SetStatus(IsPointRole(item) ? "Кликни на снимке. Точка сохранится в карточке."
                    : "Проведи прямоугольник на снимке. Область сохранится в карточке.");
            }
            catch (Exception error) { SetStatus(error.Message); }
            finally { working = false; Activate(); }
        }

        private void OpenShot()
        {
            if (working || Current == null) return;
            var dialog = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                bool full = Current.Kind == MarkerKind.Viewport;
                using (var loaded = new Bitmap(dialog.FileName))
                {
                    Bitmap image = full ? new Bitmap(loaded) : new Bitmap(loaded, DesktopSurface.FrameWidth, DesktopSurface.FrameHeight);
                    SetFrame(image, full);
                }
                SetStatus("Кадр из файла. Сохраняй расположение только для соответствующего игрового вида.");
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private void SetFrame(Bitmap image, bool full)
        {
            if (frame != null) frame.Dispose();
            frame = image;
            raw = full;
            areaOffset = null;
            areaDraft.Clear();
            preview.Source = Theme.Image(frame);
            outline.Visibility = Visibility.Collapsed;
            ShowSavedPoints();
        }

        private void BeginSelection(object sender, MouseButtonEventArgs args)
        {
            if (working || frame == null || Current == null || raw != (Current.Kind == MarkerKind.Viewport))
            {
                SetStatus("Сними кадр для выбранного элемента.");
                return;
            }
            WpfPoint position = args.GetPosition(canvas);
            Rect bounds = ImageBounds();
            if (bounds.IsEmpty) return;
            if (IsPointRole(Current) && !bounds.Contains(position))
            {
                SetStatus("Кликни внутри снимка.");
                return;
            }
            origin = Clamp(position);
            selecting = true;
            canvas.CaptureMouse();
            UpdateOutline(origin);
        }

        private void UpdateSelection(object sender, MouseEventArgs args)
        {
            if (selecting) UpdateOutline(Clamp(args.GetPosition(canvas)));
        }

        private void FinishSelection(object sender, MouseButtonEventArgs args)
        {
            if (!selecting) return;
            selecting = false;
            canvas.ReleaseMouseCapture();
            MarkerItem item = Current;
            Rect bounds = ImageBounds();
            if (item == null || bounds.IsEmpty || raw != (item.Kind == MarkerKind.Viewport)) return;
            WpfPoint end = Clamp(args.GetPosition(canvas));
            bool click = IsPointRole(item)
                && Math.Abs(origin.X - end.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(origin.Y - end.Y) < SystemParameters.MinimumVerticalDragDistance;
            PixelRect selection;
            if (click)
            {
                outline.Visibility = Visibility.Collapsed;
                int size = IsToolGrip(item) ? GripBoxSize : CenterBoxSize;
                int x = Math.Min(frame.Width - 1, (int)((end.X - bounds.X) / bounds.Width * frame.Width));
                int y = Math.Min(frame.Height - 1, (int)((end.Y - bounds.Y) / bounds.Height * frame.Height));
                selection = new PixelRect(x - size / 2, y - size / 2, size, size);
            }
            else
            {
                UpdateOutline(end);
                int left = (int)((Math.Min(origin.X, end.X) - bounds.X) / bounds.Width * frame.Width);
                int top = (int)((Math.Min(origin.Y, end.Y) - bounds.Y) / bounds.Height * frame.Height);
                int width = (int)(Math.Abs(origin.X - end.X) / bounds.Width * frame.Width);
                int height = (int)(Math.Abs(origin.Y - end.Y) / bounds.Height * frame.Height);
                selection = new PixelRect(left, top, width, height);
            }

            if (selection.Width < 7 || selection.Height < 7 || !new PixelRect(0, 0, frame.Width, frame.Height).Contains(selection))
            {
                SetStatus(click ? "Точка слишком близко к краю снимка." : "Область слишком мала. Проведи прямоугольник чуть шире.");
                return;
            }
            try
            {
                if (item.Kind == MarkerKind.Field)
                {
                    MarkAreaCorner(selection);
                    return;
                }
                VisualRegion area = VisualRegion.FromRectangle(selection, frame.Width, frame.Height);
                if (item.Kind == MarkerKind.Viewport)
                {
                    if (Math.Abs(selection.Width / (double)selection.Height - 16.0 / 9) > 0.115)
                        throw new CycleStop("Область Android должна иметь пропорцию примерно 16:9.");
                    calibration.Viewport = area;
                }
                ApplySelection(item, area, selection);
                store.Write(path, calibration);
                ShowSavedPoints();
                SetStatus(item.Name + " сохранён · осталось " + calibration.Missing().Length);
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private void MarkAreaCorner(PixelRect selection)
        {
            PixelPoint offset = AreaOffset();
            selection.Offset(-offset.X, -offset.Y);
            VisualRegion corner = VisualRegion.FromRectangle(selection, frame.Width, frame.Height);
            if (areaDraft.Count < 2)
            {
                areaDraft.Add(corner);
                ShowSavedPoints();
                SetStatus(areaDraft.Count == 1 ? "Теперь отметь верхний угол." : "Теперь отметь нижний угол.");
                return;
            }
            SaveArea(areaDraft[0], areaDraft[1], corner);
            areaDraft.Clear();
            ShowSavedPoints();
            SetStatus("Область сохранена · " + calibration.PlotPoints().Length + " точек прохода");
        }

        private void SaveStep()
        {
            if (calibration.Field == null || areaDraft.Count != 0) return;
            try
            {
                SaveArea(calibration.Field.Left, calibration.Field.Top, calibration.Field.Bottom);
                SetStatus("Шаг сохранён · " + calibration.PlotPoints().Length + " точек прохода");
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private void SaveArea(VisualRegion left, VisualRegion top, VisualRegion bottom)
        {
            int step;
            if (!Int32.TryParse(areaStep.Text, out step)) throw new CycleStop("Введи шаг от 10 до 80 пикселей.");
            var area = new FieldArea { Left = left, Top = top, Bottom = bottom, Step = step };
            area.Sweep(DesktopSurface.FrameWidth, DesktopSurface.FrameHeight);
            calibration.Field = area;
            store.Write(path, calibration);
        }

        private PixelPoint AreaOffset()
        {
            if (areaOffset.HasValue) return areaOffset.Value;
            VisualMarker anchor;
            if (!calibration.Markers.TryGetValue("anchor", out anchor)) return PixelPoint.Empty;
            PixelRect expected = ScreenVision.Base(frame, anchor);
            PixelRect? visible = ScreenVision.Locate(frame, anchor, expected, Math.Max(frame.Width, frame.Height), 23);
            if (!visible.HasValue) throw new CycleStop("На кадре не найден ориентир. Сними ферму с видимым ориентиром.");
            areaOffset = new PixelPoint(visible.Value.X - expected.X, visible.Value.Y - expected.Y);
            return areaOffset.Value;
        }

        private void ApplySelection(MarkerItem item, VisualRegion area, PixelRect selection)
        {
            switch (item.Kind)
            {
                case MarkerKind.Marker: calibration.Markers[item.Key] = ScreenVision.Make(frame, selection); break;
                case MarkerKind.Counter: calibration.Counters[item.Key] = area; break;
                case MarkerKind.Plot: AddPoint(calibration.Plots, area, 30); break;
                case MarkerKind.Slot: AddPoint(calibration.Slots, area, 24); break;
            }
        }

        private void AddPoint(System.Collections.Generic.List<VisualRegion> points, VisualRegion area, int limit)
        {
            if (points.Count >= limit) throw new CycleStop("Достигнут лимит точек для одного кадра.");
            if (points.Any(point => Math.Abs(point.X + point.Width / 2 - area.X - area.Width / 2) < 0.008
                && Math.Abs(point.Y + point.Height / 2 - area.Y - area.Height / 2) < 0.01))
                throw new CycleStop("Эта точка уже добавлена.");
            points.Add(area);
        }

        private void RemoveLast()
        {
            if (Current == null) return;
            try
            {
                if (Current.Kind == MarkerKind.Field)
                {
                    if (areaDraft.Count > 0) areaDraft.RemoveAt(areaDraft.Count - 1);
                    else if (calibration.Field != null) { areaDraft.Add(calibration.Field.Left); areaDraft.Add(calibration.Field.Top); }
                    ShowSavedPoints();
                    SetStatus("Отметь следующий угол области.");
                    return;
                }
                var points = Current.Kind == MarkerKind.Plot ? calibration.Plots
                    : Current.Kind == MarkerKind.Slot ? calibration.Slots : null;
                if (points != null && points.Count != 0)
                    points.RemoveAt(points.Count - 1);
                else if (!IsToolGrip(Current) || !calibration.Markers.Remove(Current.Key))
                {
                    SetStatus("Выбери сохранённую группу грядок, инструмент или группу слотов.");
                    return;
                }
                store.Write(path, calibration);
                outline.Visibility = Visibility.Collapsed;
                ShowSavedPoints();
                SetStatus("");
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private void ShowSavedPoints()
        {
            savedPoints.Children.Clear();
            MarkerItem item = Current;
            Rect bounds = ImageBounds();
            if (item == null || raw || bounds.IsEmpty) return;

            if (item.Kind == MarkerKind.Field)
            {
                DrawField(bounds);
                return;
            }

            var points = item.Kind == MarkerKind.Plot ? calibration.Plots : item.Kind == MarkerKind.Slot ? calibration.Slots : null;
            if (points != null)
            {
                for (int index = 0; index < points.Count; index++)
                    AddBadge(points[index], (index + 1).ToString(), bounds);
            }
            else if (IsToolGrip(item))
            {
                VisualMarker marker;
                if (calibration.Markers.TryGetValue(item.Key, out marker) && marker != null)
                    AddBadge(marker.Region, "×", bounds);
            }
        }

        private void DrawField(Rect bounds)
        {
            if (areaDraft.Count == 0 && calibration.Field == null) return;
            PixelPoint offset;
            try { offset = AreaOffset(); }
            catch (CycleStop) { return; }
            var corners = areaDraft.Count != 0 ? areaDraft.ToArray() :
                new[] { calibration.Field.Left, calibration.Field.Top, calibration.Field.Bottom };
            WpfPoint[] screen = corners.Select(point => new WpfPoint(
                bounds.Left + bounds.Width * (point.X + point.Width / 2 + (double)offset.X / frame.Width),
                bounds.Top + bounds.Height * (point.Y + point.Height / 2 + (double)offset.Y / frame.Height))).ToArray();
            if (screen.Length == 3)
            {
                var right = new WpfPoint(screen[1].X + screen[2].X - screen[0].X, screen[1].Y + screen[2].Y - screen[0].Y);
                savedPoints.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection(new[] { screen[0], screen[1], right, screen[2] }),
                    Stroke = Theme.Brush("#5FE479"), StrokeThickness = 2, Fill = Theme.Brush("#225FE479")
                });
            }
            for (int index = 0; index < screen.Length; index++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Theme.Green };
                Canvas.SetLeft(dot, screen[index].X - 5);
                Canvas.SetTop(dot, screen[index].Y - 5);
                savedPoints.Children.Add(dot);
            }
        }

        private void AddBadge(VisualRegion region, string label, Rect bounds)
        {
            if (region == null) return;
            double x = region.X + region.Width / 2;
            double y = region.Y + region.Height / 2;
            if (x < 0 || x > 1 || y < 0 || y > 1 || Double.IsNaN(x) || Double.IsNaN(y)) return;
            const double diameter = 22;
            var badge = new Border
            {
                Width = diameter, Height = diameter, Background = Theme.Green, BorderBrush = Theme.Paper,
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(diameter / 2),
                Child = new TextBlock
                {
                    Text = label, Foreground = Theme.Paper, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            Canvas.SetLeft(badge, bounds.Left + bounds.Width * x - diameter / 2);
            Canvas.SetTop(badge, bounds.Top + bounds.Height * y - diameter / 2);
            savedPoints.Children.Add(badge);
        }

        private WpfPoint Clamp(WpfPoint value)
        {
            Rect bounds = ImageBounds();
            if (bounds.IsEmpty) return value;
            return new WpfPoint(Math.Max(bounds.Left, Math.Min(bounds.Right, value.X)), Math.Max(bounds.Top, Math.Min(bounds.Bottom, value.Y)));
        }

        private Rect ImageBounds()
        {
            if (frame == null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return Rect.Empty;
            double scale = Math.Min(canvas.ActualWidth / frame.Width, canvas.ActualHeight / frame.Height);
            double width = scale * frame.Width, height = scale * frame.Height;
            return new Rect((canvas.ActualWidth - width) / 2, (canvas.ActualHeight - height) / 2, width, height);
        }

        private void UpdateOutline(WpfPoint end)
        {
            outline.Visibility = Visibility.Visible;
            Canvas.SetLeft(outline, Math.Min(origin.X, end.X));
            Canvas.SetTop(outline, Math.Min(origin.Y, end.Y));
            outline.Width = Math.Abs(origin.X - end.X);
            outline.Height = Math.Abs(origin.Y - end.Y);
        }
    }
}
