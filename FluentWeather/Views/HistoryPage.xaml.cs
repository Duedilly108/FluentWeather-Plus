using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Services;
using FluentWeather.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace FluentWeather.Views
{
    /// <summary>
    /// Local build: the Full History page, shown by MainPage over its content. See HistoryService
    /// for where the data comes from. The charts are drawn with XAML shapes on a Canvas, with no chart
    /// library.
    ///
    /// The chart covers the chosen year. Month and Year fit it to a month or to the whole year. The
    /// mouse wheel zooms around the pointer. Shift+wheel, a sideways swipe, dragging the chart or the
    /// scroll bar below it move the view. The -, + and Fit buttons zoom without a wheel. A view of
    /// HourlyBelowDays or more shows one value per day. A shorter view shows every hour. The hours
    /// load one month at a time, only for the months in view, and only the part in view is drawn.
    /// Clicking a day zooms to that day. The table lists the part in view.
    /// </summary>
    public sealed partial class HistoryPage : Page
    {
        private const int FirstArchiveYear = 1940;

        // Views shorter than this show every hour. A month (28 to 31 days) shows days.
        private const double HourlyBelowDays = 26;

        // The shortest view is one day.
        private const double MinSpanDays = 1;

        // One wheel notch zooms by this factor. The + and - buttons zoom by the other one.
        private const double WheelZoom = 0.8;
        private const double ButtonZoom = 0.6;

        private enum ChartKind { Temperature, Wind, Precip, Air, Humidity, Pressure, Clouds }

        private enum Axis { Left, Right }

        private enum SeriesStyle { Line, Bar }

        // One series of a chart. Values may hold nulls for missing data.
        private sealed class Series
        {
            public string Name = "";
            public Color Color;
            public double?[] Values = Array.Empty<double?>();
            public SeriesStyle Style = SeriesStyle.Line;
            public Axis Axis = Axis.Left;
            public bool Labels;
        }

        private static readonly Color HighColor = Color.FromArgb(255, 0xFF, 0x96, 0x5A);
        private static readonly Color LowColor = Color.FromArgb(255, 0x78, 0xAA, 0xFF);
        private static readonly Color WindColor = Color.FromArgb(255, 0xEB, 0x78, 0xEB);
        private static readonly Color GustColor = Color.FromArgb(255, 0x8C, 0x82, 0xFF);
        private static readonly Color RainColor = Color.FromArgb(255, 0x4F, 0xA8, 0xF0);
        private static readonly Color SnowColor = Color.FromArgb(255, 0xE6, 0xEC, 0xF5);
        private static readonly Color AirColor = Color.FromArgb(255, 0x6C, 0xD1, 0x8C);
        private static readonly Color HumidityColor = Color.FromArgb(255, 0x5C, 0xC8, 0xD8);
        private static readonly Color DewColor = Color.FromArgb(255, 0x9E, 0xD8, 0x7A);
        private static readonly Color PressureColor = Color.FromArgb(255, 0xF0, 0xC0, 0x5A);
        private static readonly Color CloudColor = Color.FromArgb(255, 0xB4, 0xBC, 0xCC);
        private static readonly Color SunColor = Color.FromArgb(255, 0xFF, 0xD2, 0x4A);
        private static readonly Color GapColor = Color.FromArgb(255, 0xA0, 0xA4, 0xB4);
        private static readonly Color GridColor = Color.FromArgb(255, 0x50, 0x58, 0x6E);
        private static readonly Color MinorGridColor = Color.FromArgb(255, 0x42, 0x48, 0x5C);
        private static readonly Color NightColor = Color.FromArgb(255, 0x40, 0x46, 0x5C);
        private static readonly Color BandColor = Color.FromArgb(45, 0x78, 0xAA, 0xFF);
        private static readonly Color TextColor = Color.FromArgb(255, 0xC8, 0xCC, 0xD8);
        private static readonly Color BarbColor = Color.FromArgb(255, 0xE1, 0xE4, 0xEE);
        private static readonly Color TrackColor = Color.FromArgb(255, 0x2A, 0x2F, 0x3E);
        private static readonly Color ThumbColor = Color.FromArgb(255, 0x7A, 0x82, 0x99);
        private static readonly Color ReadoutLineColor = Color.FromArgb(200, 0xE1, 0xE4, 0xEE);

        private IReadOnlyList<SavedLocation> _locations = Array.Empty<SavedLocation>();

        // The days of the chosen year, by day of the year (0 is January 1). Null where no data is loaded.
        private HistoryDay?[] _dayAt = Array.Empty<HistoryDay?>();

        // The hours of the chosen year, by month (1 to 12), loaded when a short view needs them.
        private readonly Dictionary<int, List<HistoryHour>> _monthHours = new();
        private readonly HashSet<int> _monthHoursLoading = new();

        private DateTime _yearStart = new(DateTime.Now.Year, 1, 1);
        private int _yearDays = 365;

        // The view, in days from January 1 of the chosen year.
        private double _viewStart;
        private double _viewEnd = 31;

        // The view before a click zoomed to one day. Esc goes back to it.
        private (double Start, double End)? _beforeDayZoom;

        private DateTimeOffset _locationNow = DateTimeOffset.Now;
        private string _units = "e";
        private bool _usAqi = true;
        private bool _ready;
        private bool _loadingDays;
        private bool _changingRange;
        private int _loadNumber;
        private string? _requestedPlaceId;

        // What the last drawing showed, for the pointer readout and the table.
        private double _plotLeft;
        private double _plotTop;
        private double _plotWidth;
        private double _plotHeight;
        private bool _shownHourly;
        private readonly List<HistoryHour> _shownHours = new();
        private readonly List<int> _shownDays = new();
        private Line? _readoutLine;
        private Border? _readoutBox;
        private TextBlock? _readoutText;

        // Dragging the chart or the scroll bar.
        private bool _pressed;
        private bool _dragging;
        private bool _dragMoved;
        private double _dragStartX;
        private double _dragStartView;
        private bool _thumbDragging;
        private double _thumbGrab;

        private bool _redrawQueued;
        private readonly DispatcherTimer _tableTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        public HistoryPage()
        {
            InitializeComponent();
            Loaded += HistoryPage_Loaded;
            _tableTimer.Tick += (_, _) =>
            {
                _tableTimer.Stop();
                FillTable();
            };
        }

        /// <summary>Raised by the back button and by Esc. MainPage hides the page.</summary>
        public event EventHandler? CloseRequested;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _requestedPlaceId = e.Parameter as string;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        // Esc first goes back from a day that a click zoomed to, then back to the month or year,
        // then closes the page.
        private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (_beforeDayZoom is { } before)
            {
                _beforeDayZoom = null;
                SetView(before.Start, before.End);
            }
            else if (!IsPresetView())
                FitPreset();
            else
                CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Selects a location when the page is already open.</summary>
        public void ShowLocation(string? placeId)
        {
            if (string.IsNullOrEmpty(placeId) || !_ready)
            {
                _requestedPlaceId = placeId;
                return;
            }

            var index = IndexOfLocation(placeId);
            if (index >= 0 && LocationCombo.SelectedIndex != index)
                LocationCombo.SelectedIndex = index;
        }

        private int IndexOfLocation(string? placeId)
        {
            for (var i = 0; i < _locations.Count; i++)
                if (_locations[i].PlaceId == placeId)
                    return i;
            return -1;
        }

        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_ready)
                return;

            try
            {
                _units = (await VariousUtils.GetUnitsCode()).ToString();
                _locations = await SavedLocationsService.GetAllAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "HistoryPage.Loaded");
                _locations = Array.Empty<SavedLocation>();
            }

            foreach (var location in _locations)
                LocationCombo.Items.Add(location.FullName.Length > 0 ? location.FullName : location.Name);

            var now = DateTime.Now;
            for (var year = now.Year; year >= FirstArchiveYear; year--)
                YearCombo.Items.Add(year.ToString(CultureInfo.CurrentCulture));

            var monthNames = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;
            for (var month = 1; month <= 12; month++)
                MonthCombo.Items.Add(monthNames[month - 1]);

            if (_locations.Count == 0)
            {
                StatusText.Text = "HistoryNoLocations".GetLocalized();
                ChartEmptyText.Text = "HistoryNoData".GetLocalized();
                ChartEmptyText.Visibility = Visibility.Visible;
                _ready = true;
                return;
            }

            var index = IndexOfLocation(_requestedPlaceId);
            LocationCombo.SelectedIndex = index >= 0 ? index : 0;
            YearCombo.SelectedIndex = 0;
            MonthCombo.SelectedIndex = now.Month - 1;
            _ready = true;
            await LoadYearAsync();
        }

        private async void Reload_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
                await LoadYearAsync();
        }

        private void Month_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && !IsYearView)
                FitPreset();
        }

        private void Range_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;
            MonthCombo.Visibility = IsYearView ? Visibility.Collapsed : Visibility.Visible;
            if (!_changingRange)
                FitPreset();
        }

        private void Chart_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
                RequestRedraw();
        }

        private bool IsYearView => RangeSegmented.SelectedIndex == 1;

        private int SelectedYear => DateTime.Now.Year - Math.Max(0, YearCombo.SelectedIndex);

        private int SelectedMonth => Math.Max(0, MonthCombo.SelectedIndex) + 1;

        private ChartKind Kind => (ChartKind) Math.Max(0, ChartSegmented.SelectedIndex);

        private SavedLocation? Location => LocationCombo.SelectedIndex >= 0 && LocationCombo.SelectedIndex < _locations.Count
            ? _locations[LocationCombo.SelectedIndex]
            : null;

        // --- The view ---

        private (double Start, double End) PresetView()
        {
            if (IsYearView)
                return (0, _yearDays);
            var first = new DateTime(_yearStart.Year, SelectedMonth, 1);
            var start = (first - _yearStart).TotalDays;
            return (start, start + DateTime.DaysInMonth(first.Year, first.Month));
        }

        private bool IsPresetView()
        {
            var (start, end) = PresetView();
            return Math.Abs(_viewStart - start) < 0.01 && Math.Abs(_viewEnd - end) < 0.01;
        }

        // Month or Year: the chosen month, or the whole year.
        private void FitPreset()
        {
            _beforeDayZoom = null;
            var (start, end) = PresetView();
            SetView(start, end);
        }

        private void SetView(double start, double end)
        {
            var span = Math.Clamp(end - start, Math.Min(MinSpanDays, _yearDays), _yearDays);
            start = Math.Clamp(start, 0, _yearDays - span);
            _viewStart = start;
            _viewEnd = start + span;
            RequestRedraw();
            EnsureHoursForView();
        }

        // Zooms by a factor, keeping the point at "anchor" (days from January 1) in its place.
        private void ZoomAround(double anchor, double factor)
        {
            var span = _viewEnd - _viewStart;
            var newSpan = Math.Clamp(span * factor, Math.Min(MinSpanDays, _yearDays), _yearDays);
            var ratio = span > 0 ? (anchor - _viewStart) / span : 0.5;
            var start = anchor - ratio * newSpan;
            SetView(start, start + newSpan);
        }

        private double TimeAt(double x) =>
            _plotWidth <= 0 ? _viewStart : _viewStart + (x - _plotLeft) / _plotWidth * (_viewEnd - _viewStart);

        // The months (1 to 12) that the view touches.
        private IEnumerable<int> MonthsInView(double margin = 0)
        {
            var first = _yearStart.AddDays(Math.Max(0, Math.Floor(_viewStart - margin)));
            var last = _yearStart.AddDays(Math.Min(_yearDays - 1, Math.Ceiling(_viewEnd + margin) - 1));
            if (first.Year != _yearStart.Year)
                yield break;
            for (var m = first.Month; m <= (last.Year == _yearStart.Year ? last.Month : 12); m++)
                yield return m;
        }

        private bool IsFutureMonth(int month) => new DateTime(_yearStart.Year, month, 1) >= _locationNow.Date;

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAround((_viewStart + _viewEnd) / 2, ButtonZoom);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAround((_viewStart + _viewEnd) / 2, 1 / ButtonZoom);

        // Fit shows the whole year.
        private void ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            _beforeDayZoom = null;
            if (!IsYearView)
            {
                _changingRange = true;
                RangeSegmented.SelectedIndex = 1;
                _changingRange = false;
            }
            SetView(0, _yearDays);
        }

        // --- Loading ---

        private async Task LoadYearAsync()
        {
            var location = Location;
            if (location == null || YearCombo.SelectedIndex < 0 || MonthCombo.SelectedIndex < 0)
                return;

            var load = ++_loadNumber;
            var year = SelectedYear;
            _yearStart = new DateTime(year, 1, 1);
            _yearDays = DateTime.IsLeapYear(year) ? 366 : 365;
            _dayAt = new HistoryDay?[_yearDays];
            _monthHours.Clear();
            _monthHoursLoading.Clear();
            _usAqi = location.FullName.Contains("United States", StringComparison.OrdinalIgnoreCase);
            _loadingDays = true;
            UpdateStatus();

            try
            {
                _locationNow = await PastDaysService.GetLocationNowAsync(location.PlaceId);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "HistoryPage.LoadYearAsync");
                _locationNow = DateTimeOffset.Now;
            }
            if (load != _loadNumber)
                return;

            FitPreset();

            // The month in view first, so it shows at once. Then the rest of the year, nearest first.
            var focus = IsYearView ? 12 : SelectedMonth;
            foreach (var month in Enumerable.Range(1, 12).OrderBy(m => Math.Abs(m - focus)).ThenBy(m => m))
            {
                if (IsFutureMonth(month))
                    continue;

                List<HistoryDay> days;
                try
                {
                    days = await HistoryService.GetMonthAsync(location.PlaceId, year, month, _units, _locationNow);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "HistoryPage.LoadYearAsync");
                    days = new List<HistoryDay>();
                }
                if (load != _loadNumber)
                    return; // a newer selection is loading

                foreach (var day in days)
                {
                    var i = (ParseDate(day.Date) - _yearStart).Days;
                    if (i >= 0 && i < _yearDays)
                        _dayAt[i] = day;
                }
                if (MonthsInView().Contains(month))
                    RequestRedraw();
            }

            _loadingDays = false;
            RequestRedraw();
        }

        // Loads the hours of the months in a short view, one month at a time.
        private async void EnsureHoursForView()
        {
            if (_viewEnd - _viewStart >= HourlyBelowDays)
                return;
            var location = Location;
            if (location == null)
                return;

            var load = _loadNumber;
            var year = _yearStart.Year;
            foreach (var month in MonthsInView(1).ToList())
            {
                if (IsFutureMonth(month) || _monthHours.ContainsKey(month) || !_monthHoursLoading.Add(month))
                    continue;
                UpdateStatus();

                List<HistoryHour> hours;
                try
                {
                    hours = await HistoryService.GetMonthHoursAsync(location.PlaceId, year, month, _units, _locationNow);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "HistoryPage.EnsureHoursForView");
                    hours = new List<HistoryHour>();
                }
                if (load != _loadNumber)
                    return;

                _monthHoursLoading.Remove(month);
                _monthHours[month] = hours;
                RequestRedraw();
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_locations.Count == 0)
                return;
            LoadingRing.IsActive = _loadingDays || _monthHoursLoading.Count > 0;
            if (_monthHoursLoading.Count > 0)
            {
                StatusText.Text = "HistoryLoadingHours".GetLocalized();
                return;
            }

            var days = DaysInView().Where(d => d.HasTemperature).ToList();
            if (days.Count == 0)
            {
                StatusText.Text = _loadingDays ? "HistoryLoading".GetLocalized() : "";
                return;
            }
            var recorded = days.Count(d => d.Source == HistorySource.Recorded);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "HistoryStatus".GetLocalized(), days.Count, recorded, days.Count - recorded);
        }

        private IEnumerable<HistoryDay> DaysInView()
        {
            var first = Math.Max(0, (int) Math.Floor(_viewStart));
            var last = Math.Min(_dayAt.Length - 1, (int) Math.Ceiling(_viewEnd) - 1);
            for (var i = first; i <= last; i++)
                if (_dayAt[i] is { } day)
                    yield return day;
        }

        private void RequestRedraw()
        {
            if (_redrawQueued)
                return;
            _redrawQueued = true;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _redrawQueued = false;
                DrawChart();
                UpdateStatus();
                _tableTimer.Stop();
                _tableTimer.Start();
            });
        }

        // --- Pointer: wheel zoom, drag, readout, click ---

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ZoomHint.Visibility = e.NewSize.Width < 1050 ? Visibility.Collapsed : Visibility.Visible;
            DrawChart();
        }

        private void ChartCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ChartCanvas);
            var delta = point.Properties.MouseWheelDelta;
            if (delta == 0 || _plotWidth <= 0)
                return;
            e.Handled = true;
            HideReadout();

            var span = _viewEnd - _viewStart;
            var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
            if (point.Properties.IsHorizontalMouseWheel || shift)
            {
                // A sideways swipe to the right, or Shift with the wheel turned down, moves to later days.
                var move = (point.Properties.IsHorizontalMouseWheel ? delta : -delta) / 120.0 * span * 0.15;
                SetView(_viewStart + move, _viewEnd + move);
                return;
            }

            _beforeDayZoom = null;
            var anchor = Math.Clamp(TimeAt(point.Position.X), _viewStart, _viewEnd);
            ZoomAround(anchor, Math.Pow(WheelZoom, delta / 120.0));
        }

        // A press starts a possible drag. The drag begins when the pointer moves 4 pixels, so a
        // plain click still counts as a click.
        private void ChartCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ChartCanvas);
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
                return;
            _pressed = true;
            _dragging = false;
            _dragMoved = false;
            _dragStartX = point.Position.X;
            _dragStartView = _viewStart;
        }

        private void ChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ChartCanvas);
            if (!_pressed || !point.IsInContact)
            {
                _pressed = false;
                ShowReadout(point.Position);
                return;
            }

            var dx = point.Position.X - _dragStartX;
            if (!_dragMoved && Math.Abs(dx) < 4)
                return;
            if (!_dragMoved)
            {
                _dragMoved = true;
                _dragging = ChartCanvas.CapturePointer(e.Pointer);
                HideReadout();
            }
            var span = _viewEnd - _viewStart;
            var start = _dragStartView - (_plotWidth > 0 ? dx / _plotWidth * span : 0);
            SetView(start, start + span);
        }

        private void ChartCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _pressed = false;
            if (!_dragging)
                return;
            _dragging = false;
            ChartCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void ChartCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _pressed = false;
            _dragging = false;
        }

        private void ChartCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
                HideReadout();
        }

        // A click on a day zooms to that day. Esc goes back.
        private void ChartCanvas_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_dragMoved)
            {
                _dragMoved = false;
                return;
            }
            if (_shownHourly)
                return;

            var day = (int) Math.Floor(TimeAt(e.GetPosition(ChartCanvas).X));
            if (day < 0 || day >= _dayAt.Length || _dayAt[day] == null)
                return;
            _beforeDayZoom ??= (_viewStart, _viewEnd);
            HideReadout();
            SetView(day, day + 1);
        }

        private void ShowReadout(Point position)
        {
            if (_readoutLine == null || _readoutBox == null || _readoutText == null)
                return;
            if (position.X < _plotLeft || position.X > _plotLeft + _plotWidth || position.Y < _plotTop || position.Y > _plotTop + _plotHeight)
            {
                HideReadout();
                return;
            }

            var t = TimeAt(position.X);
            double at;
            string text;
            if (_shownHourly)
            {
                if (_shownHours.Count == 0)
                {
                    HideReadout();
                    return;
                }
                var target = _yearStart.AddDays(t);
                var hour = _shownHours.MinBy(h => Math.Abs((h.Time - target).TotalMinutes))!;
                at = (hour.Time - _yearStart).TotalDays;
                text = HourTip(hour);
            }
            else
            {
                var day = (int) Math.Floor(t);
                if (day < 0 || day >= _dayAt.Length || _dayAt[day] == null)
                {
                    HideReadout();
                    return;
                }
                at = day + 0.5;
                text = DayTip(_dayAt[day]!);
            }

            var x = _plotLeft + (at - _viewStart) / (_viewEnd - _viewStart) * _plotWidth;
            _readoutLine.X1 = x;
            _readoutLine.X2 = x;
            _readoutLine.Visibility = Visibility.Visible;
            _readoutText.Text = text;
            _readoutBox.Visibility = Visibility.Visible;
            _readoutBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = _readoutBox.DesiredSize.Width;
            var left = x + 12;
            if (left + width > ChartCanvas.ActualWidth)
                left = x - 12 - width;
            Canvas.SetLeft(_readoutBox, Math.Max(0, left));
            Canvas.SetTop(_readoutBox, _plotTop + 4);
        }

        private void HideReadout()
        {
            if (_readoutLine != null)
                _readoutLine.Visibility = Visibility.Collapsed;
            if (_readoutBox != null)
                _readoutBox.Visibility = Visibility.Collapsed;
        }

        // --- Scroll bar ---

        private double ScrollTimeAt(double x) => _plotWidth <= 0 ? 0 : (x - _plotLeft) / _plotWidth * _yearDays;

        private void ScrollCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ScrollCanvas);
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
                return;
            var t = ScrollTimeAt(point.Position.X);
            var span = _viewEnd - _viewStart;

            // On the thumb: drag it. On the track: center the view there, then drag.
            if (t >= _viewStart && t <= _viewEnd)
                _thumbGrab = t - _viewStart;
            else
            {
                _thumbGrab = span / 2;
                SetView(t - _thumbGrab, t - _thumbGrab + span);
            }
            _thumbDragging = ScrollCanvas.CapturePointer(e.Pointer);
        }

        private void ScrollCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_thumbDragging)
                return;
            var span = _viewEnd - _viewStart;
            var start = ScrollTimeAt(e.GetCurrentPoint(ScrollCanvas).Position.X) - _thumbGrab;
            SetView(start, start + span);
        }

        private void ScrollCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_thumbDragging)
                return;
            _thumbDragging = false;
            ScrollCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void ScrollCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _thumbDragging = false;

        private void DrawScrollBar()
        {
            var canvas = ScrollCanvas;
            canvas.Children.Clear();
            if (_plotWidth <= 0 || _yearDays <= 0)
                return;

            var track = new Rectangle { Width = _plotWidth, Height = 8, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(TrackColor) };
            Canvas.SetLeft(track, _plotLeft);
            Canvas.SetTop(track, 2);
            canvas.Children.Add(track);

            var width = Math.Max(16, (_viewEnd - _viewStart) / _yearDays * _plotWidth);
            var left = _plotLeft + _viewStart / _yearDays * _plotWidth;
            left = Math.Min(left, _plotLeft + _plotWidth - width);
            var thumb = new Rectangle { Width = width, Height = 8, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(ThumbColor) };
            Canvas.SetLeft(thumb, left);
            Canvas.SetTop(thumb, 2);
            canvas.Children.Add(thumb);
        }

        // --- Units ---

        private WetUnits Units => _units switch { "m" => WetUnits.m, "h" => WetUnits.h, "s" => WetUnits.s, _ => WetUnits.e };
        private string Degrees => "°" + MeasureUnitUtils.GetTemperatureUnits(Units);
        private string RainUnit => MeasureUnitUtils.GetLiquidPrecipitationUnits(Units);
        private string SnowUnit => MeasureUnitUtils.GetSnowPrecipitationUnits(Units);
        private string WindUnit => MeasureUnitUtils.GetWindSpeedUnits(Units);
        private string PressureUnit => _units == "e" ? "inHg" : "hPa";
        private string AqiName => (_usAqi ? "HistoryUsAqi" : "HistoryEuAqi").GetLocalized();

        // --- Chart content per kind ---

        private (string Title, List<Series> Series, double?[]? Directions) BuildChart(bool hourly)
        {
            var series = new List<Series>();
            double?[]? directions = null;
            string title;
            double?[] D(Func<HistoryDay, double?> f) => _shownDays.Select(i => _dayAt[i] is { } day ? f(day) : null).ToArray();
            double?[] H(Func<HistoryHour, double?> f) => _shownHours.Select(f).ToArray();

            // Value labels when the points are far enough apart: up to 31 days, or 2 days of hours.
            var labels = hourly ? _viewEnd - _viewStart <= 2 : _viewEnd - _viewStart <= 31;

            switch (Kind)
            {
                case ChartKind.Wind:
                    title = string.Format(CultureInfo.CurrentCulture, (hourly ? "HistoryTitleWindHourly" : "HistoryTitleWind").GetLocalized(), WindUnit);
                    series.Add(new Series { Name = "HistoryLegendGusts".GetLocalized(), Color = GustColor, Labels = labels,
                        Values = hourly ? H(h => h.Gust) : D(d => d.GustMax) });
                    series.Add(new Series { Name = "HistoryLegendWind".GetLocalized(), Color = WindColor, Labels = labels,
                        Values = hourly ? H(h => h.Wind) : D(d => d.WindMax) });
                    directions = hourly ? H(h => h.WindDirection) : D(d => d.WindDirection);
                    break;
                case ChartKind.Precip:
                    title = string.Format(CultureInfo.CurrentCulture, (hourly ? "HistoryTitlePrecipHourly" : "HistoryTitlePrecip").GetLocalized(), RainUnit, SnowUnit);
                    series.Add(new Series { Name = "HistoryLegendPrecip".GetLocalized(), Color = RainColor, Style = SeriesStyle.Bar,
                        Values = hourly ? H(h => h.Precip) : D(d => d.Precip) });
                    series.Add(new Series { Name = "HistoryLegendSnow".GetLocalized(), Color = SnowColor, Style = SeriesStyle.Bar,
                        Values = hourly ? H(h => h.Snow) : D(d => d.Snow) });
                    break;
                case ChartKind.Air:
                    title = string.Format(CultureInfo.CurrentCulture, (hourly ? "HistoryTitleAirHourly" : "HistoryTitleAir").GetLocalized(), AqiName);
                    series.Add(new Series { Name = AqiName, Color = AirColor, Labels = labels,
                        Values = hourly ? H(h => _usAqi ? h.UsAqi : h.EuropeanAqi) : D(d => _usAqi ? d.UsAqi : d.EuropeanAqi) });
                    break;
                case ChartKind.Humidity:
                    title = (hourly ? "HistoryTitleHumidityHourly" : "HistoryTitleHumidity").GetLocalized();
                    series.Add(new Series { Name = "HistoryLegendHumidity".GetLocalized(), Color = HumidityColor, Labels = labels,
                        Values = hourly ? H(h => h.Humidity) : D(d => d.Humidity) });
                    series.Add(new Series { Name = "HistoryLegendDewPoint".GetLocalized() + " (" + Degrees + ")", Color = DewColor, Axis = Axis.Right,
                        Values = hourly ? H(h => h.DewPoint) : D(d => d.DewPoint) });
                    break;
                case ChartKind.Pressure:
                    title = string.Format(CultureInfo.CurrentCulture, (hourly ? "HistoryTitlePressureHourly" : "HistoryTitlePressure").GetLocalized(), PressureUnit);
                    series.Add(new Series { Name = "HistoryLegendPressure".GetLocalized(), Color = PressureColor, Labels = labels,
                        Values = hourly ? H(h => h.Pressure) : D(d => d.Pressure) });
                    break;
                case ChartKind.Clouds:
                    title = (hourly ? "HistoryTitleCloudsHourly" : "HistoryTitleClouds").GetLocalized();
                    if (!hourly)
                        series.Add(new Series { Name = "HistoryLegendSun".GetLocalized(), Color = SunColor, Style = SeriesStyle.Bar, Axis = Axis.Right,
                            Values = D(d => d.SunHours) });
                    series.Add(new Series { Name = "HistoryLegendCloud".GetLocalized(), Color = CloudColor, Labels = labels,
                        Values = hourly ? H(h => h.CloudCover) : D(d => d.CloudCover) });
                    break;
                default:
                    title = string.Format(CultureInfo.CurrentCulture, (hourly ? "HistoryTitleTemperatureHourly" : "HistoryChartTitle").GetLocalized(),
                        MeasureUnitUtils.GetTemperatureUnits(Units));
                    if (hourly)
                    {
                        series.Add(new Series { Name = "HistoryLegendTemperature".GetLocalized(), Color = HighColor, Labels = labels, Values = H(h => h.Temperature) });
                        series.Add(new Series { Name = "HistoryLegendDewPoint".GetLocalized(), Color = DewColor, Values = H(h => h.DewPoint) });
                    }
                    else
                    {
                        series.Add(new Series { Name = "HistoryLegendHigh".GetLocalized(), Color = HighColor, Labels = labels, Values = D(d => d.High) });
                        series.Add(new Series { Name = "HistoryLegendLow".GetLocalized(), Color = LowColor, Labels = labels, Values = D(d => d.Low) });
                    }
                    break;
            }

            return (title, series, directions);
        }

        // The dates in view, for the chart title: "November 2026", "2026", one day, or "November 3 to November 11, 2026".
        private string RangeText()
        {
            var ci = CultureInfo.CurrentCulture;
            var first = _yearStart.AddDays(Math.Floor(_viewStart + 1e-6));
            var last = _yearStart.AddDays(Math.Max(Math.Floor(_viewStart + 1e-6), Math.Ceiling(_viewEnd - 1e-6) - 1));
            if (first == last)
                return first.ToString("D", ci);
            if (first == _yearStart && last == _yearStart.AddDays(_yearDays - 1))
                return first.Year.ToString(ci);
            if (first.Day == 1 && last == first.AddMonths(1).AddDays(-1))
                return first.ToString("Y", ci);
            return string.Format(ci, "HistoryRangeTo".GetLocalized(), first.ToString("M", ci), last.ToString("M", ci)) + ", " +
                   first.Year.ToString(ci);
        }

        // --- Chart drawing ---

        private void DrawChart()
        {
            var canvas = ChartCanvas;
            canvas.Children.Clear();
            LegendPanel.Children.Clear();
            _readoutLine = null;
            _readoutBox = null;
            _readoutText = null;

            var span = _viewEnd - _viewStart;
            if (span <= 0 || _dayAt.Length == 0)
                return;

            // A short view shows hours once one month of them is loaded. Until then it shows days.
            var hourly = span < HourlyBelowDays && MonthsInView().Any(m => _monthHours.TryGetValue(m, out var list) && list.Count > 0);
            _shownHourly = hourly;
            _shownHours.Clear();
            _shownDays.Clear();

            // The points: the ones in view and one more on each side, so the lines run to the edges.
            var ts = new List<double>();
            if (hourly)
            {
                foreach (var month in MonthsInView(1))
                {
                    if (!_monthHours.TryGetValue(month, out var list))
                        continue;
                    foreach (var hour in list)
                    {
                        var t = (hour.Time - _yearStart).TotalDays;
                        if (t < _viewStart - 1.0 / 24 || t > _viewEnd + 1.0 / 24)
                            continue;
                        _shownHours.Add(hour);
                        ts.Add(t);
                    }
                }
            }
            else
            {
                var firstDay = Math.Max(0, (int) Math.Floor(_viewStart) - 1);
                var lastDay = Math.Min(_yearDays - 1, (int) Math.Ceiling(_viewEnd));
                for (var i = firstDay; i <= lastDay; i++)
                {
                    _shownDays.Add(i);
                    ts.Add(i + 0.5);
                }
            }

            var count = ts.Count;
            var (title, series, directions) = BuildChart(hourly);
            ChartTitle.Text = title + ", " + RangeText();
            foreach (var s in series)
                LegendPanel.Children.Add(LegendBox(s.Name, s.Color));

            bool InView(int i) => ts[i] >= _viewStart && ts[i] <= _viewEnd;

            var width = canvas.ActualWidth;
            var height = canvas.ActualHeight;
            var hasRight = series.Any(s => s.Axis == Axis.Right && s.Values.Any(v => v != null));
            var barbs = directions != null && directions.Any(v => v != null);
            double left = 40, right = hasRight ? 40 : 10, top = 22, bottom = barbs ? 52 : 24;
            _plotLeft = left;
            _plotTop = top;
            _plotWidth = Math.Max(0, width - left - right);
            _plotHeight = Math.Max(0, height - top - bottom);
            DrawScrollBar();

            var hasData = series.Any(s => s.Values.Where((_, i) => InView(i)).Any(v => v != null));
            ChartEmptyText.Text = Kind == ChartKind.Air ? "HistoryNoAirData".GetLocalized() : "HistoryNoData".GetLocalized();
            ChartEmptyText.Visibility = hasData || _loadingDays ? Visibility.Collapsed : Visibility.Visible;
            if (width < 80 || height < 60 || count == 0 || !hasData || _plotWidth < 40 || _plotHeight < 30)
                return;

            var plotWidth = _plotWidth;
            var plotHeight = _plotHeight;
            var pxPerDay = plotWidth / span;
            var slot = hourly ? pxPerDay / 24 : pxPerDay;
            double X(int i) => (ts[i] - _viewStart) * pxPerDay;
            double XAt(double t) => (t - _viewStart) * pxPerDay;

            // The scales use the points in view only, so they follow the view.
            List<Series> InViewSeries(Axis axis) => series.Where(s => s.Axis == axis)
                .Select(s => new Series { Style = s.Style, Axis = s.Axis, Values = s.Values.Where((_, i) => InView(i)).ToArray() }).ToList();
            var leftScale = AxisScale(InViewSeries(Axis.Left));
            var rightScale = hasRight ? AxisScale(InViewSeries(Axis.Right)) : leftScale;
            double YL(double v) => (leftScale.Max - v) / (leftScale.Max - leftScale.Min) * plotHeight;
            double YR(double v) => (rightScale.Max - v) / (rightScale.Max - rightScale.Min) * plotHeight;

            var grid = new SolidColorBrush(GridColor);
            var minor = new SolidColorBrush(MinorGridColor);
            var text = new SolidColorBrush(TextColor);

            // The plot area. Its clip cuts the lines at the edges of the view.
            var plot = new Canvas
            {
                Width = plotWidth,
                Height = plotHeight,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, plotWidth, plotHeight) },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(plot, left);
            Canvas.SetTop(plot, top);
            canvas.Children.Add(plot);

            // Night hours, joined into one band per night.
            if (hourly)
            {
                for (var i = 0; i < count; i++)
                {
                    if (!_shownHours[i].IsNight)
                        continue;
                    var j = i;
                    while (j + 1 < count && _shownHours[j + 1].IsNight && ts[j + 1] - ts[j] < 1.5 / 24)
                        j++;
                    var x0 = X(i) - slot / 2;
                    var r = new Rectangle { Width = Math.Max(1, X(j) - X(i) + slot), Height = plotHeight, Fill = new SolidColorBrush(NightColor) };
                    Canvas.SetLeft(r, x0);
                    plot.Children.Add(r);
                    i = j;
                }
            }

            // Vertical lines: midnights in the hourly view, days in a short daily view, month starts in a long one.
            void VerticalLine(double x) =>
                plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = plotHeight, Stroke = minor, StrokeThickness = 1 });
            if (hourly)
            {
                for (var d = Math.Ceiling(_viewStart); d < _viewEnd; d++)
                    VerticalLine(XAt(d));
            }
            else if (span <= 62)
            {
                for (var i = 0; i < count; i++)
                    VerticalLine(X(i));
            }
            else
            {
                for (var m = 1; m <= 12; m++)
                {
                    var d = (new DateTime(_yearStart.Year, m, 1) - _yearStart).TotalDays;
                    if (d > _viewStart && d < _viewEnd)
                        VerticalLine(XAt(d));
                }
            }

            // Horizontal lines and the axis labels.
            for (var v = leftScale.Min; v <= leftScale.Max + leftScale.Step / 2; v += leftScale.Step)
            {
                var y = YL(v);
                plot.Children.Add(new Line { X1 = 0, X2 = plotWidth, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                AddText(canvas, FormatAxis(v, leftScale.Step), 0, top + y - 8, text);
            }
            if (hasRight)
            {
                for (var v = rightScale.Min; v <= rightScale.Max + rightScale.Step / 2; v += rightScale.Step)
                    AddText(canvas, FormatAxis(v, rightScale.Step), left + plotWidth + 6, top + YR(v) - 8, text);
            }

            DrawTimeLabels(canvas, hourly, pxPerDay, top + plotHeight + (barbs ? 30 : 4), text);

            // Temperature band between the low and the high.
            if (!hourly && Kind == ChartKind.Temperature && series.Count == 2)
                DrawBand(plot, series[0].Values, series[1].Values, X, YL);

            // Bars, side by side when there are two bar series.
            var bars = series.Where(s => s.Style == SeriesStyle.Bar).ToList();
            for (var b = 0; b < bars.Count; b++)
            {
                var s = bars[b];
                var barWidth = Math.Max(1.5, slot * 0.7 / bars.Count);
                for (var i = 0; i < count; i++)
                {
                    var v = s.Values[i];
                    if (v == null || v <= 0)
                        continue;
                    var y0 = s.Axis == Axis.Right ? YR(Math.Max(rightScale.Min, 0)) : YL(Math.Max(leftScale.Min, 0));
                    var y1 = s.Axis == Axis.Right ? YR(v.Value) : YL(v.Value);
                    var r = new Rectangle { Width = barWidth, Height = Math.Max(1, y0 - y1), Fill = new SolidColorBrush(s.Color) };
                    Canvas.SetLeft(r, X(i) - slot * 0.35 + b * barWidth);
                    Canvas.SetTop(r, y1);
                    plot.Children.Add(r);
                }
            }

            // Lines with markers and value labels.
            var markers = hourly ? span * 24 <= 60 : span <= 62;
            bool LabelAt(int i) => InView(i) && (hourly ? _shownHours[i].Time.Hour % 3 == 0 : _shownDays[i] % 3 == 0);
            foreach (var s in series.Where(s => s.Style == SeriesStyle.Line))
            {
                Func<double, double> y = s.Axis == Axis.Right ? YR : YL;
                DrawLine(plot, s, ts, X, y, hourly ? 1.5 / 24 : 1.5, markers, LabelAt);
            }

            // Wind barbs below the plot, at least about 22 pixels apart.
            if (barbs)
            {
                var speeds = series.Last().Values;
                var every = hourly
                    ? new[] { 1, 2, 3, 6, 12, 24 }.FirstOrDefault(k => k * slot >= 22)
                    : Math.Max(1, (int) Math.Ceiling(22 / slot));
                if (every == 0)
                    every = 24;
                var length = Math.Min(18, Math.Max(10, slot * every * 0.8));
                for (var i = 0; i < count; i++)
                {
                    if (!InView(i) || directions![i] == null || speeds[i] == null)
                        continue;
                    var step = hourly ? _shownHours[i].Time.Hour : _shownDays[i];
                    if (step % every != 0)
                        continue;
                    DrawBarb(canvas, left + X(i), top + plotHeight + 14, speeds[i]!.Value, directions[i]!.Value, length);
                }
            }

            // The readout under the pointer: a line and a box like the app's hover panels.
            _readoutLine = new Line
            {
                Y1 = top, Y2 = top + plotHeight, Stroke = new SolidColorBrush(ReadoutLineColor), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            canvas.Children.Add(_readoutLine);
            _readoutText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap };
            _readoutBox = new Border
            {
                Child = _readoutText,
                Padding = new Thickness(9, 6, 9, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(255, 0x2B, 0x2B, 0x2D), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(255, 0x2B, 0x35, 0x47), Offset = 1 }
                    }
                },
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            canvas.Children.Add(_readoutBox);
        }

        // The time labels below the plot.
        private void DrawTimeLabels(Canvas canvas, bool hourly, double pxPerDay, double y, Brush brush)
        {
            var ci = CultureInfo.CurrentCulture;
            var left = _plotLeft;
            double XAt(double t) => left + (t - _viewStart) * pxPerDay;
            var twentyFourHour = ci.DateTimeFormat.ShortTimePattern.Contains("H");

            if (hourly)
            {
                // Hours when there is room for them, with the day name at midnight. Otherwise one label per day.
                var pxPerHour = pxPerDay / 24;
                var hourStep = new[] { 1, 2, 3, 6, 12 }.FirstOrDefault(k => k * pxPerHour >= 40);
                if (hourStep > 0)
                {
                    for (var index = (long) Math.Ceiling(_viewStart * 24); index <= _viewEnd * 24; index++)
                    {
                        var t = index / 24.0;
                        var time = _yearStart.AddHours(index);
                        if (time.Hour % hourStep != 0)
                            continue;
                        var label = time.Hour == 0
                            ? time.ToString("ddd d", ci)
                            : time.ToString(twentyFourHour ? "HH:mm" : "h tt", ci).ToLowerInvariant();
                        AddText(canvas, label, XAt(t) - 14, y, brush);
                    }
                    return;
                }

                for (var d = Math.Floor(_viewStart); d < _viewEnd; d++)
                {
                    var noon = d + 0.5;
                    if (noon < _viewStart || noon > _viewEnd)
                        continue;
                    var date = _yearStart.AddDays(d);
                    string? label = pxPerDay >= 54 ? date.ToString("ddd d", ci)
                        : pxPerDay >= 20 ? (date.Day == 1 ? date.ToString("MMM d", ci) : date.Day.ToString(ci))
                        : date.Day % 5 == 1 ? date.Day.ToString(ci) : null;
                    if (label != null)
                        AddText(canvas, label, XAt(noon) - (pxPerDay >= 54 ? 18 : 6), y, brush);
                }
                return;
            }

            if (_viewEnd - _viewStart > 62)
            {
                // Month names at the month starts.
                for (var m = 1; m <= 12; m++)
                {
                    var d = (new DateTime(_yearStart.Year, m, 1) - _yearStart).TotalDays;
                    if (d + 4 < _viewStart || d > _viewEnd - 4)
                        continue;
                    AddText(canvas, new DateTime(_yearStart.Year, m, 1).ToString("MMM", ci), XAt(Math.Max(d, _viewStart)) + 4, y, brush);
                }
                return;
            }

            // Day numbers, as many as fit.
            var every = new[] { 1, 2, 3, 5, 7, 10, 14 }.FirstOrDefault(k => k * pxPerDay >= 26);
            if (every == 0)
                every = 14;
            for (var d = Math.Floor(_viewStart); d < _viewEnd; d++)
            {
                var center = d + 0.5;
                if (center < _viewStart || center > _viewEnd)
                    continue;
                var date = _yearStart.AddDays(d);
                if ((date.Day - 1) % every != 0)
                    continue;
                var label = date.Day == 1 && every > 1 ? date.ToString("MMM d", ci) : date.Day.ToString(ci);
                AddText(canvas, label, XAt(center) - 6, y, brush);
            }
        }

        private (double Min, double Max, double Step) AxisScale(List<Series> list)
        {
            var values = list.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).ToList();
            if (values.Count == 0)
                return (0, 1, 1);
            var bars = list.Any(s => s.Style == SeriesStyle.Bar);
            var min = bars || Kind is ChartKind.Wind or ChartKind.Precip or ChartKind.Air or ChartKind.Clouds ? 0 : values.Min();
            var max = values.Max();
            if (Kind is ChartKind.Humidity or ChartKind.Clouds && list.All(s => s.Axis == Axis.Left))
            {
                min = 0;
                max = 100;
            }
            var range = Math.Max(max - min, 1e-6);
            var step = NiceStep(range / 5);
            var lo = Math.Floor(min / step) * step;
            var hi = Math.Ceiling(max / step) * step;
            if (hi <= lo)
                hi = lo + step;
            return (lo, hi, step);
        }

        private static double NiceStep(double raw)
        {
            var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var f = raw / power;
            var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
            return nice * power;
        }

        private static string FormatAxis(double v, double step) =>
            v.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0", CultureInfo.CurrentCulture);

        // A line through the points. A gap longer than maxGap (in days) is bridged by a dashed grey line.
        private void DrawLine(Canvas canvas, Series s, List<double> ts, Func<int, double> x, Func<double, double> y,
            double maxGap, bool markers, Func<int, bool> labelAt)
        {
            var brush = new SolidColorBrush(s.Color);
            Polyline? line = null;
            int? lastIndex = null;

            for (var i = 0; i < ts.Count; i++)
            {
                var value = s.Values[i];
                if (value == null)
                    continue;

                var point = new Point(x(i), y(value.Value));
                if (lastIndex != null && ts[i] - ts[lastIndex.Value] > maxGap && line != null)
                {
                    var from = line.Points[line.Points.Count - 1];
                    canvas.Children.Add(new Line
                    {
                        X1 = from.X, Y1 = from.Y, X2 = point.X, Y2 = point.Y,
                        Stroke = new SolidColorBrush(GapColor), StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 3, 3 }
                    });
                    line = null;
                }

                if (line == null)
                {
                    line = new Polyline { Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                    canvas.Children.Add(line);
                }
                line.Points.Add(point);
                lastIndex = i;

                if (markers || line.Points.Count == 1)
                {
                    var m = new Rectangle { Width = 5, Height = 5, Fill = brush };
                    Canvas.SetLeft(m, point.X - 2.5);
                    Canvas.SetTop(m, point.Y - 2.5);
                    canvas.Children.Add(m);
                }

                if (s.Labels && labelAt(i))
                {
                    var label = new TextBlock
                    {
                        Text = value.Value.ToString(Kind == ChartKind.Pressure && _units == "e" ? "0.00" : "0", CultureInfo.CurrentCulture),
                        FontSize = 11, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = brush
                    };
                    Canvas.SetLeft(label, point.X - 8);
                    Canvas.SetTop(label, point.Y - 19);
                    canvas.Children.Add(label);
                }
            }
        }

        private static void DrawBand(Canvas canvas, double?[] high, double?[] low, Func<int, double> x, Func<double, double> y)
        {
            var run = new List<int>();
            for (var i = 0; i <= high.Length; i++)
            {
                if (i < high.Length && high[i] != null && low[i] != null)
                {
                    run.Add(i);
                    continue;
                }
                if (run.Count > 1)
                {
                    var polygon = new Polygon { Fill = new SolidColorBrush(BandColor) };
                    foreach (var k in run) polygon.Points.Add(new Point(x(k), y(high[k]!.Value)));
                    for (var k = run.Count - 1; k >= 0; k--) polygon.Points.Add(new Point(x(run[k]), y(low[run[k]]!.Value)));
                    canvas.Children.Add(polygon);
                }
                run.Clear();
            }
        }

        // A standard wind barb. The staff points to where the wind comes from. A long feather is
        // 10 knots, a short one 5 knots, a triangle 50 knots. Speeds are in the units of the page.
        private void DrawBarb(Canvas canvas, double x, double y, double speed, double fromDegrees, double length)
        {
            var knots = Units switch
            {
                WetUnits.e or WetUnits.h => speed * 0.868976,
                WetUnits.s => speed * 1.943844,
                _ => speed * 0.539957
            };
            var brush = new SolidColorBrush(BarbColor);
            var a = fromDegrees * Math.PI / 180;
            double ux = Math.Sin(a), uy = -Math.Cos(a);
            double tx = x + ux * length, ty = y + uy * length;

            if (knots < 2.5)
            {
                var calm = new Ellipse { Width = 6, Height = 6, Stroke = brush, StrokeThickness = 1 };
                Canvas.SetLeft(calm, x - 3);
                Canvas.SetTop(calm, y - 3);
                canvas.Children.Add(calm);
                return;
            }

            canvas.Children.Add(new Line { X1 = x, Y1 = y, X2 = tx, Y2 = ty, Stroke = brush, StrokeThickness = 1.2 });
            double px = -uy, py = ux;
            var pos = 0.0;
            var rest = Math.Round(knots / 5) * 5;
            while (rest >= 50)
            {
                var flag = new Polygon { Fill = brush };
                flag.Points.Add(new Point(tx - ux * pos, ty - uy * pos));
                flag.Points.Add(new Point(tx - ux * pos + px * 7, ty - uy * pos + py * 7));
                flag.Points.Add(new Point(tx - ux * (pos + 4), ty - uy * (pos + 4)));
                canvas.Children.Add(flag);
                pos += 5;
                rest -= 50;
            }
            while (rest >= 10)
            {
                canvas.Children.Add(new Line
                {
                    X1 = tx - ux * pos, Y1 = ty - uy * pos,
                    X2 = tx - ux * pos + px * 7 + ux * 2.5, Y2 = ty - uy * pos + py * 7 + uy * 2.5,
                    Stroke = brush, StrokeThickness = 1.2
                });
                pos += 3;
                rest -= 10;
            }
            if (rest >= 5)
            {
                if (pos == 0) pos = 3;
                canvas.Children.Add(new Line
                {
                    X1 = tx - ux * pos, Y1 = ty - uy * pos,
                    X2 = tx - ux * pos + px * 3.5 + ux * 1.2, Y2 = ty - uy * pos + py * 3.5 + uy * 1.2,
                    Stroke = brush, StrokeThickness = 1.2
                });
            }
        }

        private static Border LegendBox(string name, Color color) =>
            new()
            {
                Background = new SolidColorBrush(color),
                Padding = new Thickness(8, 1, 8, 2),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    Text = name, FontSize = 11, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x16, 0x24))
                }
            };

        private static void AddText(Canvas canvas, string value, double x, double y, Brush brush)
        {
            var t = new TextBlock { Text = value, FontSize = 11, Foreground = brush, IsHitTestVisible = false };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            canvas.Children.Add(t);
        }

        // --- Readout text ---

        private string DayTip(HistoryDay d)
        {
            var lines = new List<string> { ParseDate(d.Date).ToString("D", CultureInfo.CurrentCulture) };
            if (d.HasTemperature) lines.Add(Fmt(d.High, Degrees) + " / " + Fmt(d.Low, Degrees) + (string.IsNullOrEmpty(d.Phrase) ? "" : "  " + d.Phrase));
            lines.Add("HistoryColPrecip".GetLocalized() + " " + Fmt(d.Precip, " " + RainUnit) + "   " + "HistoryColSnow".GetLocalized() + " " + Fmt(d.Snow, " " + SnowUnit));
            lines.Add("HistoryColWind".GetLocalized() + " " + Fmt(d.WindMax, " " + WindUnit) + "   " + "HistoryColGust".GetLocalized() + " " + Fmt(d.GustMax, " " + WindUnit) + "   " + Cardinal(d.WindDirection));
            if (d.UsAqi != null || d.EuropeanAqi != null) lines.Add(AqiName + " " + Fmt(_usAqi ? d.UsAqi : d.EuropeanAqi, ""));
            lines.Add("HistoryClickForHours".GetLocalized());
            return string.Join("\n", lines);
        }

        private string HourTip(HistoryHour h)
        {
            var lines = new List<string> { h.Time.ToString("g", CultureInfo.CurrentCulture) + (string.IsNullOrEmpty(h.Phrase) ? "" : "  " + h.Phrase) };
            lines.Add(Fmt(h.Temperature, Degrees) + "   " + "HistoryColHumidity".GetLocalized() + " " + Fmt(h.Humidity, "%"));
            lines.Add("HistoryColWind".GetLocalized() + " " + Fmt(h.Wind, " " + WindUnit) + "   " + "HistoryColGust".GetLocalized() + " " + Fmt(h.Gust, " " + WindUnit) + "   " + Cardinal(h.WindDirection));
            lines.Add("HistoryColPrecip".GetLocalized() + " " + Fmt(h.Precip, " " + RainUnit) + "   " + "HistoryColSnow".GetLocalized() + " " + Fmt(h.Snow, " " + SnowUnit));
            if (h.UsAqi != null || h.EuropeanAqi != null) lines.Add(AqiName + " " + Fmt(_usAqi ? h.UsAqi : h.EuropeanAqi, ""));
            return string.Join("\n", lines);
        }

        // --- Table ---
        // The table lists the part of the chart in view: days, newest first, or hours, oldest first.
        // The rows are filled only when they scroll into view, so a month of hours stays light.

        private sealed class Column
        {
            public string Header = "";
            public double Width;
            public Func<int, string> Text = _ => "";
            public bool Dim;
        }

        private bool _tableHourly;
        private List<HistoryDay> _tableDays = new();
        private List<HistoryHour> _tableHours = new();
        private List<Column> _columns = new();
        private int _columnsVersion;
        private const double ConditionsWidth = 130;

        private List<Column> Columns(bool hourly)
        {
            var list = new List<Column>();
            void Add(string key, double width, Func<int, string> text, bool dim = false) =>
                list.Add(new Column { Header = key.StartsWith("History", StringComparison.Ordinal) ? key.GetLocalized() : key, Width = width, Text = text, Dim = dim });

            if (hourly)
            {
                Add("HistoryColTime", 110, i => _tableHours[i].Time.ToString("ddd d, t", CultureInfo.CurrentCulture));
                Add("HistoryColTemperature", 64, i => Fmt(_tableHours[i].Temperature, Degrees));
                Add("HistoryColDewPoint", 72, i => Fmt(_tableHours[i].DewPoint, Degrees));
                Add("HistoryColHumidity", 72, i => Fmt(_tableHours[i].Humidity, "%"));
                Add("HistoryColPrecip", 76, i => Fmt(_tableHours[i].Precip, " " + RainUnit));
                Add("HistoryColSnow", 70, i => Fmt(_tableHours[i].Snow, " " + SnowUnit));
                Add("HistoryColWind", 74, i => Fmt(_tableHours[i].Wind, " " + WindUnit));
                Add("HistoryColGust", 74, i => Fmt(_tableHours[i].Gust, " " + WindUnit));
                Add("HistoryColFrom", 50, i => Cardinal(_tableHours[i].WindDirection));
                Add("HistoryColPressure", 86, i => Fmt(_tableHours[i].Pressure, " " + PressureUnit));
                Add("HistoryColCloud", 58, i => Fmt(_tableHours[i].CloudCover, "%"));
                Add(AqiName, 64, i => Fmt(_usAqi ? _tableHours[i].UsAqi : _tableHours[i].EuropeanAqi, ""));
                Add("HistoryColConditions", 0, i => _tableHours[i].Phrase ?? "");
            }
            else
            {
                Add("HistoryColDate", 96, i => ParseDate(_tableDays[i].Date).ToString("ddd d MMM", CultureInfo.CurrentCulture));
                Add("HistoryColHigh", 56, i => Fmt(_tableDays[i].High, Degrees));
                Add("HistoryColLow", 56, i => Fmt(_tableDays[i].Low, Degrees));
                Add("HistoryColPrecip", 76, i => Fmt(_tableDays[i].Precip, " " + RainUnit));
                Add("HistoryColSnow", 70, i => Fmt(_tableDays[i].Snow, " " + SnowUnit));
                Add("HistoryColWind", 74, i => Fmt(_tableDays[i].WindMax, " " + WindUnit));
                Add("HistoryColGust", 74, i => Fmt(_tableDays[i].GustMax, " " + WindUnit));
                Add("HistoryColFrom", 50, i => Cardinal(_tableDays[i].WindDirection));
                Add("HistoryColHumidity", 72, i => Fmt(_tableDays[i].Humidity, "%"));
                Add("HistoryColDewPoint", 72, i => Fmt(_tableDays[i].DewPoint, Degrees));
                Add("HistoryColPressure", 86, i => Fmt(_tableDays[i].Pressure, " " + PressureUnit));
                Add("HistoryColCloud", 58, i => Fmt(_tableDays[i].CloudCover, "%"));
                Add("HistoryColSun", 58, i => Fmt(_tableDays[i].SunHours, " h"));
                Add(AqiName, 64, i => Fmt(_usAqi ? _tableDays[i].UsAqi : _tableDays[i].EuropeanAqi, ""));
                Add("HistoryColConditions", 0, i => _tableDays[i].Phrase ?? "");
                Add("HistoryColSource", 80, i => (_tableDays[i].Source == HistorySource.Recorded ? "HistorySourceRecorded" : "HistorySourceArchive").GetLocalized(), true);
            }
            return list;
        }

        private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            TableGrid.Width = Math.Max(TableGrid.MinWidth, e.NewSize.Width);
            TableGrid.Height = e.NewSize.Height;
        }

        private void FillTable()
        {
            _tableHourly = _shownHourly;
            if (_tableHourly)
            {
                _tableHours = _shownHours.Where(h =>
                {
                    var t = (h.Time - _yearStart).TotalDays;
                    return t >= _viewStart && t <= _viewEnd;
                }).ToList();
            }
            else
            {
                _tableDays = DaysInView().Where(d => d.HasTemperature || d.Precip != null || d.WindMax != null).Reverse().ToList();
            }

            _columns = Columns(_tableHourly);
            _columnsVersion++;
            TableGrid.MinWidth = _columns.Sum(c => c.Width > 0 ? c.Width : ConditionsWidth) + 40;
            TableGrid.Width = Math.Max(TableGrid.MinWidth, TableScroll.ActualWidth);

            TableHeader.Children.Clear();
            var header = new Grid();
            FillRow(header, -1);
            TableHeader.Children.Add(header);

            TableList.Items.Clear();
            var rows = _tableHourly ? _tableHours.Count : _tableDays.Count;
            for (var i = 0; i < rows; i++)
                TableList.Items.Add(i);
        }

        private void TableList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid row || args.Item is not int index)
                return;
            FillRow(row, index);
            args.Handled = true;
        }

        // Fills one row of the table. Index -1 is the header.
        private void FillRow(Grid row, int index)
        {
            var header = index < 0;
            if (row.Tag is not int version || version != _columnsVersion)
            {
                var style = (Style) Resources[header ? "HistoryHeaderCellStyle" : "HistoryCellStyle"];
                row.Children.Clear();
                row.ColumnDefinitions.Clear();
                for (var c = 0; c < _columns.Count; c++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = _columns[c].Width > 0 ? new GridLength(_columns[c].Width) : new GridLength(1, GridUnitType.Star),
                        MinWidth = _columns[c].Width > 0 ? 0 : ConditionsWidth
                    });
                    var cell = new TextBlock { Style = style };
                    if (!header && _columns[c].Dim)
                        cell.Opacity = 0.7;
                    Grid.SetColumn(cell, c);
                    row.Children.Add(cell);
                }
                row.Tag = _columnsVersion;
            }

            var count = _tableHourly ? _tableHours.Count : _tableDays.Count;
            for (var c = 0; c < _columns.Count && c < row.Children.Count; c++)
            {
                if (row.Children[c] is TextBlock cell)
                    cell.Text = header ? _columns[c].Header : index < count ? _columns[c].Text(index) : "";
            }
        }

        // --- Formatting ---

        private static string Fmt(int? value, string unit) =>
            value == null ? "-" : value.Value.ToString(CultureInfo.CurrentCulture) + unit;

        private static string Fmt(double? value, string unit) =>
            value == null ? "-" : value.Value.ToString("0.##", CultureInfo.CurrentCulture) + unit;

        private static string Cardinal(double? degrees) => degrees == null ? "-" : WaterService.Cardinal(degrees.Value);

        private static DateTime ParseDate(string key) =>
            DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
