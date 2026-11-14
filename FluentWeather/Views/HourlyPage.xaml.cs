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
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace FluentWeather.Views
{
    /// <summary>
    /// Local build: the 10-Day Hourly page, shown by MainPage over its content. It lists every hour
    /// of the hourly forecast that the app already loads (about 240 hours from weather.com).
    ///
    /// The chart shows all hours, with a label for each day. The chart picker chooses the values.
    /// A click on the chart scrolls the list to that hour. "List" shows the main values of each hour,
    /// and a click on a row opens the other values. "Table" shows every value in its own column.
    /// The chart is drawn with XAML shapes on a Canvas, with no chart library.
    ///
    /// MainPage gives the data through DataProvider. The shown location comes from the data that
    /// MainPage holds. Another saved location is loaded the same way MainPage loads it.
    /// </summary>
    public sealed partial class HourlyPage : Page
    {
        private enum ChartKind { Overview, Temperature, FeelsLike, Precip, Wind, Gusts, Humidity, Uv, Clouds, Visibility, DewPoint, Pressure }

        private enum Axis { Left, Right }

        private enum SeriesStyle { Line, Dashed, Thin, Bar }

        private sealed class Series
        {
            public string Name = "";
            public Color Color;
            public double?[] Values = Array.Empty<double?>();
            public SeriesStyle Style = SeriesStyle.Line;
            public Axis Axis = Axis.Left;

            /// <summary>Labels the highest and lowest value of each day.</summary>
            public bool Labels;
        }

        private sealed class Day
        {
            public int Start;
            public int Count;
            public DateTime Date;
            public int High;
            public int Low;
        }

        private static readonly Color HighColor = Color.FromArgb(255, 0xFF, 0x96, 0x5A);
        private static readonly Color FeelsColor = Color.FromArgb(255, 0xE1, 0xE4, 0xEE);
        private static readonly Color WindColor = Color.FromArgb(255, 0xEB, 0x78, 0xEB);
        private static readonly Color GustColor = Color.FromArgb(255, 0x8C, 0x82, 0xFF);
        private static readonly Color RainColor = Color.FromArgb(255, 0x4F, 0xA8, 0xF0);
        private static readonly Color ChanceColor = Color.FromArgb(150, 0x4F, 0xA8, 0xF0);
        private static readonly Color SnowColor = Color.FromArgb(255, 0xE6, 0xEC, 0xF5);
        private static readonly Color HumidityColor = Color.FromArgb(255, 0x5C, 0xC8, 0xD8);
        private static readonly Color DewColor = Color.FromArgb(255, 0x9E, 0xD8, 0x7A);
        private static readonly Color PressureColor = Color.FromArgb(255, 0xF0, 0xC0, 0x5A);
        private static readonly Color CloudColor = Color.FromArgb(255, 0xB4, 0xBC, 0xCC);
        private static readonly Color SunColor = Color.FromArgb(255, 0xFF, 0xD2, 0x4A);
        private static readonly Color GridColor = Color.FromArgb(255, 0x50, 0x58, 0x6E);
        private static readonly Color NightColor = Color.FromArgb(255, 0x2E, 0x33, 0x44);
        private static readonly Color TextColor = Color.FromArgb(255, 0xC8, 0xCC, 0xD8);
        private static readonly Color PillColor = Color.FromArgb(255, 0x9F, 0xD0, 0xFF);
        private static readonly Color PillTextColor = Color.FromArgb(255, 0x10, 0x23, 0x3A);
        private static readonly Color ReadoutLineColor = Color.FromArgb(200, 0xE1, 0xE4, 0xEE);

        private static readonly Brush DayHeaderBrush = new SolidColorBrush(Color.FromArgb(40, 0x9F, 0xD0, 0xFF));
        private static readonly Brush OpenRowBrush = new SolidColorBrush(Color.FromArgb(26, 0xFF, 0xFF, 0xFF));
        private static readonly Brush WetRowBrush = new SolidColorBrush(Color.FromArgb(28, 0x4F, 0xA8, 0xF0));
        private static readonly Brush DetailLineBrush = new SolidColorBrush(Color.FromArgb(40, 0xFF, 0xFF, 0xFF));

        // A table row is tinted from this chance of precipitation.
        private const int WetChance = 40;

        // A row reaches 8 pixels into the padding of its list item on both sides and has the same
        // padding inside. Its text lines up with the header, and its tint reaches the item edges.
        private static readonly Thickness RowMargin = new(-8, 0, -8, 0);

        // Row kinds, kept in the Tag of a row with the layout version.
        private const int KindDay = 1;
        private const int KindList = 2;
        private const int KindTable = 3;

        /// <summary>Set by MainPage: the forecast of a place and when it was loaded.</summary>
        public Func<string, Task<(RootV3Response? Data, DateTimeOffset FetchedAt)>>? DataProvider { get; set; }

        /// <summary>Set by MainPage: the name of the shown location, used when it is not a saved location.</summary>
        public string? DisplayedPlaceName { get; set; }

        /// <summary>Raised by the back button and by Esc. MainPage hides the page.</summary>
        public event EventHandler? CloseRequested;

        private readonly List<(string PlaceId, string Name)> _places = new();
        private string? _requestedPlaceId;
        private bool _ready;
        private int _loadNumber;

        private V3WxForecastHourly? _h;
        private int _count;
        private readonly List<Day> _days = new();
        private WetUnits _units = WetUnits.e;
        private bool _is12Hour = true;

        // The rows of the list. 0 and up is an hour. Below 0 is the heading of day -(code + 1).
        private readonly List<int> _rows = new();
        private readonly List<object> _items = new();
        private readonly HashSet<int> _open = new();
        private int _layoutVersion;
        private readonly Dictionary<int, SvgImageSource> _icons = new();
        private readonly Dictionary<string, SvgImageSource> _drops = new();

        // The chart.
        private double _plotLeft;
        private double _plotTop;
        private double _plotWidth;
        private double _plotHeight;
        private Line? _readoutLine;
        private Border? _readoutBox;
        private TextBlock? _readoutText;
        private readonly List<(Border Box, TextBlock Text)> _dayLabels = new();
        private int _topDay;
        private ScrollViewer? _listScroll;

        public HourlyPage()
        {
            InitializeComponent();
            Loaded += HourlyPage_Loaded;
            HoursList.Loaded += HoursList_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _requestedPlaceId = e.Parameter as string;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private bool IsTable => ViewSegmented.SelectedIndex == 1;

        private ChartKind Kind => (ChartKind) Math.Max(0, ChartSegmented.SelectedIndex);

        private async void HourlyPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_ready)
                return;

            IReadOnlyList<SavedLocation> saved = Array.Empty<SavedLocation>();
            try
            {
                _units = await VariousUtils.GetUnitsCode();
                _is12Hour = await VariousUtils.Is12HourAsync();
                saved = await SavedLocationsService.GetAllAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "HourlyPage.Loaded");
            }

            // The shown location comes first when it is not a saved location.
            if (!string.IsNullOrEmpty(_requestedPlaceId) && saved.All(l => l.PlaceId != _requestedPlaceId))
                _places.Add((_requestedPlaceId!, string.IsNullOrEmpty(DisplayedPlaceName) ? "HourlyPageShownPlace".GetLocalized() : DisplayedPlaceName!));
            foreach (var location in saved)
                _places.Add((location.PlaceId, location.FullName.Length > 0 ? location.FullName : location.Name));

            foreach (var place in _places)
                LocationCombo.Items.Add(place.Name);

            if (_places.Count == 0)
            {
                ShowEmpty();
                _ready = true;
                return;
            }

            var index = _places.FindIndex(p => p.PlaceId == _requestedPlaceId);
            LocationCombo.SelectedIndex = index >= 0 ? index : 0;
            _ready = true;
            await LoadAsync();
        }

        private async void Location_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
                await LoadAsync();
        }

        private void View_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
                FillList(keepPosition: true);
        }

        private void Chart_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready)
                DrawChart();
        }

        // --- Data ---

        private async Task LoadAsync()
        {
            var index = LocationCombo.SelectedIndex;
            if (index < 0 || index >= _places.Count || DataProvider == null)
            {
                ShowEmpty();
                return;
            }

            var load = ++_loadNumber;
            LoadingRing.IsActive = true;
            RootV3Response? data = null;
            var fetched = DateTimeOffset.Now;
            try
            {
                (data, fetched) = await DataProvider(_places[index].PlaceId);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "HourlyPage.Load");
            }

            if (load != _loadNumber)
                return;
            LoadingRing.IsActive = false;

            _h = data?.v3wxforecasthourly10day;
            _count = CountHours(_h);
            _open.Clear();
            BuildDays();

            if (_h == null || _count == 0)
            {
                ShowEmpty();
                return;
            }

            var source = "weather.com";
            try
            {
                if (await DataSourceService.IsBackupActiveAsync())
                    source = "Open-Meteo";
            }
            catch (Exception)
            {
                // The name of the source stays weather.com.
            }
            if (load != _loadNumber)
                return;

            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "HourlyPageStatus".GetLocalized(), source,
                fetched.ToLocalTime().ToString(_is12Hour ? "h:mm tt" : "HH:mm", CultureInfo.InvariantCulture));
            ChartEmptyText.Visibility = Visibility.Collapsed;
            _topDay = 0;
            FillList(keepPosition: false);
            DrawChart();
        }

        // The number of hours that every list of the forecast holds.
        private static int CountHours(V3WxForecastHourly? h)
        {
            if (h?.validTimeLocal == null)
                return 0;
            var counts = new[]
            {
                h.validTimeLocal.Count, h.temperature?.Count ?? 0, h.temperatureFeelsLike?.Count ?? 0, h.iconCode?.Count ?? 0,
                h.wxPhraseLong?.Count ?? 0, h.precipChance?.Count ?? 0, h.precipType?.Count ?? 0, h.qpf?.Count ?? 0,
                h.qpfSnow?.Count ?? 0, h.relativeHumidity?.Count ?? 0, h.temperatureDewPoint?.Count ?? 0,
                h.windSpeed?.Count ?? 0, h.windDirection?.Count ?? 0, h.windDirectionCardinal?.Count ?? 0, h.windGust?.Count ?? 0,
                h.cloudCover?.Count ?? 0, h.uvIndex?.Count ?? 0, h.uvDescription?.Count ?? 0, h.visibility?.Count ?? 0,
                h.pressureMeanSeaLevel?.Count ?? 0, h.dayOrNight?.Count ?? 0
            };
            return counts.Min();
        }

        private void BuildDays()
        {
            _days.Clear();
            if (_h == null)
                return;
            for (var i = 0; i < _count; i++)
            {
                var date = _h.validTimeLocal[i].DateTime.Date;
                if (_days.Count == 0 || _days[^1].Date != date)
                    _days.Add(new Day { Start = i, Date = date, High = int.MinValue, Low = int.MaxValue });
                var day = _days[^1];
                day.Count++;
                day.High = Math.Max(day.High, _h.temperature[i]);
                day.Low = Math.Min(day.Low, _h.temperature[i]);
            }
        }

        private int DayOf(int hour)
        {
            for (var d = _days.Count - 1; d >= 0; d--)
                if (hour >= _days[d].Start)
                    return d;
            return 0;
        }

        private void ShowEmpty()
        {
            LoadingRing.IsActive = false;
            _h = null;
            _count = 0;
            _days.Clear();
            StatusText.Text = "";
            ChartTitle.Text = "";
            LegendPanel.Children.Clear();
            ChartCanvas.Children.Clear();
            ChartEmptyText.Text = "HourlyPageNoData".GetLocalized();
            ChartEmptyText.Visibility = Visibility.Visible;
            TableHeader.Children.Clear();
            HoursList.Items.Clear();
            _rows.Clear();
            _items.Clear();
        }

        // --- Units and text ---

        private static CultureInfo C => CultureInfo.CurrentCulture;

        private string Degrees => "\u00B0" + MeasureUnitUtils.GetTemperatureUnits(_units);
        private string RainUnit => MeasureUnitUtils.GetLiquidPrecipitationUnits(_units);
        private string SnowUnit => MeasureUnitUtils.GetSnowPrecipitationUnits(_units);
        private string WindUnit => MeasureUnitUtils.GetWindSpeedUnits(_units);
        private string VisibilityUnit => MeasureUnitUtils.GetVisibilityUnits(_units);
        private string PressureUnit => MeasureUnitUtils.GetPressureUnits(_units);

        // Inches show two decimals. Millimeters show one.
        private string LiquidFormat => _units == WetUnits.e ? "0.00" : "0.#";
        private string PressureFormat => _units == WetUnits.e ? "0.00" : "0.#";

        private static string L(string key) => key.GetLocalized();

        private static string F(string key, params object[] values) => string.Format(C, key.GetLocalized(), values);

        private string HourText(int i) =>
            _h!.validTimeLocal[i].DateTime.ToString(_is12Hour ? "h tt" : "HH:mm", CultureInfo.InvariantCulture);

        private double Rain(int i) => _h!.qpfRain != null && i < _h.qpfRain.Count && _h.qpfRain[i] != null ? _h.qpfRain[i]!.Value : _h.qpf[i];

        private double? Ice(int i) => _h!.qpfIce != null && i < _h.qpfIce.Count ? _h.qpfIce[i] : null;

        private int Feels(int i) => _h!.temperatureFeelsLike[i] ?? _h.temperature[i];

        private string RainText(int i) => Rain(i).ToString(LiquidFormat, C) + " " + RainUnit;

        private string SnowText(int i) => _h!.qpfSnow[i].ToString("0.#", C) + " " + SnowUnit;

        // The amount in the list: the snow when there is snow, the liquid amount otherwise.
        private string AmountText(int i) =>
            _h!.qpfSnow[i] > 0 ? F("HourlyPageAmountSnow", SnowText(i)) : _h.qpf[i].ToString(LiquidFormat, C) + " " + RainUnit;

        private string WindText(int i) => _h!.windDirectionCardinal[i] + " " + _h.windSpeed[i].ToString(C) + " " + WindUnit;

        private string TypeText(int i) => _h!.precipType[i] switch
        {
            "rain" => L("HourlyPageTypeRain"),
            "snow" => L("HourlyPageTypeSnow"),
            _ => L("HourlyPageTypePrecip")
        };

        private SvgImageSource Icon(int code)
        {
            if (!_icons.TryGetValue(code, out var source))
                _icons[code] = source = new SvgImageSource { UriSource = new Uri("ms-appx:///Assets/weticons/" + code + ".svg") };
            return source;
        }

        private SvgImageSource DropIcon(int chance)
        {
            var name = VariousUtils.GetPrecipIconChance(chance);
            if (!_drops.TryGetValue(name, out var source))
                _drops[name] = source = new SvgImageSource { UriSource = new Uri("ms-appx:///Assets/varicons/" + name + ".svg") };
            return source;
        }

        // ------------------------------------------------------------------
        // The list and the table
        // ------------------------------------------------------------------

        private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            TableGrid.Width = Math.Max(TableGrid.MinWidth, e.NewSize.Width);
            TableGrid.Height = e.NewSize.Height;
        }

        private void FillList(bool keepPosition)
        {
            if (_h == null || _count == 0)
                return;

            // The hour at the top stays at the top when List changes to Table or back.
            var top = keepPosition ? FirstVisibleRow() : -1;

            _layoutVersion++;
            TableGrid.MinWidth = IsTable ? 1212 : 910;
            TableGrid.Width = Math.Max(TableGrid.MinWidth, TableScroll.ActualWidth);

            TableHeader.Children.Clear();
            var header = new Grid();
            BuildHeader(header);
            TableHeader.Children.Add(header);

            _rows.Clear();
            _items.Clear();
            HoursList.Items.Clear();
            for (var d = 0; d < _days.Count; d++)
            {
                _rows.Add(-(d + 1));
                for (var i = _days[d].Start; i < _days[d].Start + _days[d].Count; i++)
                    _rows.Add(i);
            }
            foreach (var code in _rows)
            {
                object item = code;
                _items.Add(item);
                HoursList.Items.Add(item);
            }

            if (top > 0 && top < _items.Count)
                HoursList.ScrollIntoView(_items[top], ScrollIntoViewAlignment.Leading);
        }

        private int FirstVisibleRow() => HoursList.ItemsPanelRoot is ItemsStackPanel panel ? panel.FirstVisibleIndex : -1;

        private void HoursList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid row || args.Item is not int code)
                return;
            FillRow(row, code);
            args.Handled = true;
        }

        // A click on an hour of the list opens or closes its other values.
        private void HoursList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (IsTable || e.ClickedItem is not int code || code < 0)
                return;
            if (!_open.Remove(code))
                _open.Add(code);
            var position = _rows.IndexOf(code);
            if (position >= 0 && HoursList.ContainerFromIndex(position) is ListViewItem container &&
                container.ContentTemplateRoot is Grid row)
                FillRow(row, code);
        }

        private static TextBlock Cell(double size = 13, bool bold = false, bool dim = false, bool right = false) => new()
        {
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Opacity = dim ? 0.7 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        private static void Place(FrameworkElement element, Grid grid, int column, int row = 0, int span = 1)
        {
            Grid.SetColumn(element, column);
            Grid.SetRow(element, row);
            if (span > 1)
                Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }

        private static void Columns(Grid grid, params double[] widths)
        {
            grid.ColumnDefinitions.Clear();
            foreach (var width in widths)
            {
                // A negative width is a share of the free space, with that minimum width.
                grid.ColumnDefinitions.Add(width > 0
                    ? new ColumnDefinition { Width = new GridLength(width) }
                    : new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = -width });
            }
        }

        // List: time, icon, sky, temperature, drop, chance, amount, humidity, arrow, wind, open mark.
        private static readonly double[] ListWidths = [78, 32, -160, 72, 20, 70, 124, 96, 22, -170, 30];

        // Table: time, icon, sky, temperature, feels like, drop, chance, rain, snow, humidity, dew point,
        // arrow, wind, gust, clouds, UV, visibility, pressure.
        private static readonly double[] TableWidths = [70, 28, -150, 54, 56, 18, 58, 72, 72, 82, 68, 20, 92, 58, 68, 44, 62, 104];

        private void BuildHeader(Grid header)
        {
            TextBlock Head(string text) => new()
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            string Unit(string key, string unit) => L(key) + " (" + unit + ")";

            // The rows of the list sit 8 pixels inside the list items.
            header.Margin = new Thickness(0);
            if (IsTable)
            {
                Columns(header, TableWidths);
                Place(Head(L("HourlyPageColTime")), header, 0);
                Place(Head(L("HourlyPageColSky")), header, 1, 0, 2);
                Place(Head(L("HourlyPageColTemp")), header, 3);
                Place(Head(L("HourlyPageColFeels")), header, 4);
                Place(Head(L("HourlyPageColChance")), header, 5, 0, 2);
                Place(Head(Unit("HourlyPageColRain", RainUnit)), header, 7);
                Place(Head(Unit("HourlyPageColSnow", SnowUnit)), header, 8);
                Place(Head(L("HourlyPageColHumidity")), header, 9);
                Place(Head(L("HourlyPageColDew")), header, 10);
                Place(Head(Unit("HourlyPageColWind", WindUnit)), header, 11, 0, 2);
                Place(Head(L("HourlyPageColGust")), header, 13);
                Place(Head(L("HourlyPageColClouds")), header, 14);
                Place(Head(L("HourlyPageColUv")), header, 15);
                Place(Head(Unit("HourlyPageColVis", VisibilityUnit)), header, 16);
                Place(Head(Unit("HourlyPageColPressure", PressureUnit)), header, 17);
            }
            else
            {
                Columns(header, ListWidths);
                Place(Head(L("HourlyPageColTime")), header, 0);
                Place(Head(L("HourlyPageColSky")), header, 1, 0, 2);
                Place(Head(L("HourlyPageColTemp")), header, 3);
                Place(Head(L("HourlyPageColChance")), header, 4, 0, 2);
                Place(Head(L("HourlyPageColAmount")), header, 6);
                Place(Head(L("HourlyPageColHumidity")), header, 7);
                Place(Head(L("HourlyPageColWind")), header, 8, 0, 2);
            }
        }

        // An arrow that points where the wind blows to.
        private static TextBlock Arrow() => new()
        {
            Text = "\u2191", FontSize = 13, Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform()
        };

        private static void Turn(UIElement arrow, int fromDegrees)
        {
            if (arrow.RenderTransform is RotateTransform rotate)
                rotate.Angle = (fromDegrees + 180) % 360;
        }

        private static void SetText(UIElement element, string text)
        {
            if (element is TextBlock block)
                block.Text = text;
        }

        private static void SetSource(UIElement element, ImageSource source)
        {
            if (element is Image image)
                image.Source = source;
        }

        private static Image Picture(double size) => new()
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
        };

        // Fills one row. The parts of a row are found by their position, so a recycled row is
        // rebuilt only when its kind or the layout changed.
        private void FillRow(Grid row, int code)
        {
            if (_h == null)
                return;
            try
            {
                FillRowCore(row, code);
            }
            catch (Exception ex)
            {
                // A row that cannot be filled stays as it is. The error is logged once.
                if (!_rowErrorLogged)
                {
                    _rowErrorLogged = true;
                    CrashLog.Write(ex, "HourlyPage.FillRow");
                }
            }
        }

        private bool _rowErrorLogged;

        private void FillRowCore(Grid row, int code)
        {
            var kind = code < 0 ? KindDay : IsTable ? KindTable : KindList;
            var stamp = kind + 10 * _layoutVersion;
            if (row.Tag is not int tag || tag != stamp)
            {
                row.Children.Clear();
                row.RowDefinitions.Clear();
                row.ColumnDefinitions.Clear();
                if (kind == KindDay)
                    BuildDayRow(row);
                else if (kind == KindTable)
                    BuildTableRow(row);
                else
                    BuildListRow(row);
                row.Tag = stamp;
            }

            if (kind == KindDay)
                FillDayRow(row, -(code + 1));
            else if (code >= _count)
                return;
            else if (kind == KindTable)
                FillTableRow(row, code);
            else
                FillListRow(row, code);
        }

        // --- Day heading ---

        private static void BuildDayRow(Grid row)
        {
            row.Background = DayHeaderBrush;
            row.Margin = RowMargin;
            row.Padding = new Thickness(8, 5, 8, 6);
            row.CornerRadius = new CornerRadius(4);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(Cell(13, bold: true), row, 0);
            Place(Cell(12, dim: true), row, 1);
        }

        private void FillDayRow(Grid row, int d)
        {
            if (d < 0 || d >= _days.Count || row.Children.Count < 2)
                return;
            var day = _days[d];
            SetText(row.Children[0], day.Date.ToString("dddd, MMMM d", C));
            SetText(row.Children[1], F("HourlyPageDayHeader", day.Count, day.High, day.Low));
        }

        // --- List row ---

        private static readonly string[] DetailKeys =
        [
            "HourlyPageDetFeels", "HourlyPageDetGust", "HourlyPageColHumidity", "HourlyPageDetDew",
            "HourlyPageDetUv", "HourlyPageDetClouds", "HourlyPageDetVis", "HourlyPageColPressure",
            "HourlyPageColRain", "HourlyPageColSnow", "HourlyPageColIce", "HourlyPageColChance"
        ];

        private static void BuildListRow(Grid row)
        {
            row.Background = null;
            row.Margin = RowMargin;
            row.Padding = new Thickness(8, 0, 8, 0);
            row.CornerRadius = new CornerRadius(4);
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var main = new Grid();
            Columns(main, ListWidths);
            Place(Cell(13, bold: true), main, 0);                     // 0 time
            Place(Picture(24), main, 1);                              // 1 icon
            Place(Cell(), main, 2);                                   // 2 sky
            Place(Cell(15, bold: true), main, 3);                     // 3 temperature
            Place(Picture(13), main, 4);                              // 4 drop
            Place(Cell(), main, 5);                                   // 5 chance
            Place(Cell(), main, 6);                                   // 6 amount
            Place(Cell(), main, 7);                                   // 7 humidity
            Place(Arrow(), main, 8);                                  // 8 arrow
            Place(Cell(), main, 9);                                   // 9 wind
            Place(new FontIcon                                        // 10 open mark
            {
                FontFamily = (FontFamily) Application.Current.Resources["FluentIcons"], Glyph = "\uE70D", FontSize = 11,
                Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            }, main, 10);
            Place(main, row, 0, 0);

            // The other values: four columns of name and value. Filled when the row opens.
            var details = new Grid
            {
                Visibility = Visibility.Collapsed, Padding = new Thickness(110, 4, 30, 12), ColumnSpacing = 28, RowSpacing = 6
            };
            for (var c = 0; c < 4; c++)
                details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var r = 0; r < 3; r++)
                details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var k = 0; k < DetailKeys.Length; k++)
            {
                var pair = new Grid { BorderBrush = DetailLineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 4) };
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = Cell(12.5, dim: true);
                name.Text = L(DetailKeys[k]);
                Place(name, pair, 0);
                Place(Cell(12.5, bold: true, right: true), pair, 1);
                Place(pair, details, k % 4, k / 4);
            }
            Place(details, row, 0, 1);
        }

        private void FillListRow(Grid row, int i)
        {
            if (row.Children.Count < 2 || row.Children[0] is not Grid main || row.Children[1] is not Grid details || main.Children.Count < 11)
                return;
            var h = _h!;
            var open = _open.Contains(i);
            row.Background = open ? OpenRowBrush : null;

            SetText(main.Children[0], HourText(i));
            SetSource(main.Children[1], Icon(h.iconCode[i]));
            SetText(main.Children[2], h.wxPhraseLong[i]);
            SetText(main.Children[3], h.temperature[i].ToString(C) + "\u00B0");
            SetSource(main.Children[4], DropIcon(h.precipChance[i]));
            SetText(main.Children[5], h.precipChance[i].ToString(C) + "%");
            SetText(main.Children[6], AmountText(i));
            SetText(main.Children[7], h.relativeHumidity[i].ToString(C) + "%");
            Turn(main.Children[8], h.windDirection[i]);
            SetText(main.Children[9], WindText(i) + (h.windGust[i] is { } gust ? ", " + F("HourlyPageGusts", gust) : ""));
            if (main.Children[10] is FontIcon mark)
                mark.Glyph = open ? "\uE70E" : "\uE70D";

            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open)
                return;

            var values = new[]
            {
                Feels(i).ToString(C) + "\u00B0",
                h.windGust[i] is { } g ? g.ToString(C) + " " + WindUnit : L("HourlyPageNone"),
                h.relativeHumidity[i].ToString(C) + "%",
                h.temperatureDewPoint[i].ToString(C) + "\u00B0",
                h.uvIndex[i].ToString(C) + (string.IsNullOrEmpty(h.uvDescription[i]) ? "" : " (" + h.uvDescription[i] + ")"),
                h.cloudCover[i].ToString(C) + "%",
                h.visibility[i].ToString("0.#", C) + " " + VisibilityUnit,
                h.pressureMeanSeaLevel[i].ToString(PressureFormat, C) + " " + PressureUnit,
                RainText(i),
                SnowText(i),
                Ice(i) is { } ice ? ice.ToString(LiquidFormat, C) + " " + RainUnit : "-",
                h.precipChance[i].ToString(C) + "% " + TypeText(i)
            };
            for (var k = 0; k < values.Length && k < details.Children.Count; k++)
            {
                if (details.Children[k] is Grid pair && pair.Children.Count > 1 && pair.Children[1] is TextBlock value)
                    value.Text = values[k];
            }
        }

        // --- Table row ---

        private static void BuildTableRow(Grid row)
        {
            row.Background = null;
            row.Margin = RowMargin;
            row.Padding = new Thickness(8, 4, 8, 4);
            row.CornerRadius = new CornerRadius(3);
            Columns(row, TableWidths);
            Place(Cell(12, bold: true), row, 0);                      // 0 time
            Place(Picture(19), row, 1);                               // 1 icon
            Place(Cell(12), row, 2);                                  // 2 sky
            Place(Cell(12, bold: true), row, 3);                      // 3 temperature
            Place(Cell(12), row, 4);                                  // 4 feels like
            Place(Picture(12), row, 5);                               // 5 drop
            for (var c = 6; c <= 10; c++)                             // 6 chance, 7 rain, 8 snow, 9 humidity, 10 dew point
                Place(Cell(12), row, c);
            Place(Arrow(), row, 11);                                  // 11 arrow
            for (var c = 12; c <= 17; c++)                            // 12 wind, 13 gust, 14 clouds, 15 UV, 16 visibility, 17 pressure
                Place(Cell(12), row, c);
        }

        private void FillTableRow(Grid row, int i)
        {
            if (row.Children.Count < 18)
                return;
            var h = _h!;
            row.Background = h.precipChance[i] >= WetChance ? WetRowBrush : null;

            void Text(int child, string value, double opacity = 1)
            {
                if (row.Children[child] is TextBlock cell)
                {
                    cell.Text = value;
                    cell.Opacity = opacity;
                }
            }

            Text(0, HourText(i));
            SetSource(row.Children[1], Icon(h.iconCode[i]));
            Text(2, h.wxPhraseLong[i]);
            Text(3, h.temperature[i].ToString(C) + "\u00B0");
            Text(4, Feels(i).ToString(C) + "\u00B0");
            SetSource(row.Children[5], DropIcon(h.precipChance[i]));
            Text(6, h.precipChance[i].ToString(C) + "%");
            Text(7, Rain(i).ToString(LiquidFormat, C), Rain(i) > 0 ? 1 : 0.6);
            Text(8, h.qpfSnow[i].ToString("0.#", C), h.qpfSnow[i] > 0 ? 1 : 0.6);
            Text(9, h.relativeHumidity[i].ToString(C) + "%");
            Text(10, h.temperatureDewPoint[i].ToString(C) + "\u00B0");
            Turn(row.Children[11], h.windDirection[i]);
            Text(12, h.windDirectionCardinal[i] + " " + h.windSpeed[i].ToString(C));
            Text(13, h.windGust[i] is { } gust ? gust.ToString(C) : "-", h.windGust[i] != null ? 1 : 0.6);
            Text(14, h.cloudCover[i].ToString(C) + "%");
            Text(15, h.uvIndex[i].ToString(C));
            Text(16, h.visibility[i].ToString("0.#", C));
            Text(17, h.pressureMeanSeaLevel[i].ToString(PressureFormat, C));
        }

        // --- The day at the top of the list is marked in the chart ---

        private void HoursList_Loaded(object sender, RoutedEventArgs e)
        {
            if (_listScroll != null)
                return;
            _listScroll = FindScrollViewer(HoursList);
            if (_listScroll != null)
                _listScroll.ViewChanged += (_, _) => UpdateTopDay();
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer viewer)
                    return viewer;
                if (FindScrollViewer(child) is { } found)
                    return found;
            }
            return null;
        }

        private void UpdateTopDay()
        {
            var first = FirstVisibleRow();
            if (first < 0 || first >= _rows.Count)
                return;
            var code = _rows[first];
            var day = code < 0 ? -(code + 1) : DayOf(code);
            if (day == _topDay)
                return;
            _topDay = day;
            StyleDayLabels();
        }

        private void StyleDayLabels()
        {
            for (var d = 0; d < _dayLabels.Count; d++)
            {
                var selected = d == _topDay;
                _dayLabels[d].Box.Background = selected ? new SolidColorBrush(PillColor) : null;
                _dayLabels[d].Text.Foreground = new SolidColorBrush(selected ? PillTextColor : TextColor);
                _dayLabels[d].Text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ------------------------------------------------------------------
        // The chart
        // ------------------------------------------------------------------

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

        private double?[] Values(Func<int, double?> value)
        {
            var result = new double?[_count];
            for (var i = 0; i < _count; i++)
                result[i] = value(i);
            return result;
        }

        private (string Title, List<Series> Series) BuildChart()
        {
            var h = _h!;
            var list = new List<Series>();
            void Add(string nameKey, Color color, Func<int, double?> value, SeriesStyle style = SeriesStyle.Line, Axis axis = Axis.Left, bool labels = false) =>
                list.Add(new Series { Name = L(nameKey), Color = color, Values = Values(value), Style = style, Axis = axis, Labels = labels });

            switch (Kind)
            {
                case ChartKind.Temperature:
                    Add("HourlyPageLegendTemperature", HighColor, i => h.temperature[i], labels: true);
                    return (F("HourlyPageTitleTemperature", Degrees), list);
                case ChartKind.FeelsLike:
                    Add("HourlyPageLegendTemperature", CloudColor, i => h.temperature[i], SeriesStyle.Thin);
                    Add("HourlyPageLegendFeels", HighColor, i => Feels(i), labels: true);
                    return (F("HourlyPageTitleFeels", Degrees), list);
                case ChartKind.Precip:
                    Add("HourlyPageLegendChance", ChanceColor, i => h.precipChance[i], SeriesStyle.Thin, Axis.Right);
                    Add("HourlyPageLegendRain", RainColor, i => Rain(i), SeriesStyle.Bar);
                    Add("HourlyPageLegendSnow", SnowColor, i => h.qpfSnow[i], SeriesStyle.Bar);
                    return (F("HourlyPageTitlePrecip", RainUnit, SnowUnit), list);
                case ChartKind.Wind:
                    Add("HourlyPageLegendGust", GustColor, i => h.windGust[i]);
                    Add("HourlyPageLegendWind", WindColor, i => h.windSpeed[i]);
                    return (F("HourlyPageTitleWind", WindUnit), list);
                case ChartKind.Gusts:
                    Add("HourlyPageLegendGust", GustColor, i => h.windGust[i]);
                    return (F("HourlyPageTitleGusts", WindUnit), list);
                case ChartKind.Humidity:
                    Add("HourlyPageColHumidity", HumidityColor, i => h.relativeHumidity[i]);
                    return (L("HourlyPageTitleHumidity"), list);
                case ChartKind.Uv:
                    Add("HourlyPageDetUv", SunColor, i => h.uvIndex[i], SeriesStyle.Bar);
                    return (L("HourlyPageTitleUv"), list);
                case ChartKind.Clouds:
                    Add("HourlyPageDetClouds", CloudColor, i => h.cloudCover[i]);
                    return (L("HourlyPageTitleClouds"), list);
                case ChartKind.Visibility:
                    Add("HourlyPageDetVis", HumidityColor, i => h.visibility[i]);
                    return (F("HourlyPageTitleVisibility", VisibilityUnit), list);
                case ChartKind.DewPoint:
                    Add("HourlyPageLegendTemperature", CloudColor, i => h.temperature[i], SeriesStyle.Thin);
                    Add("HourlyPageLegendDew", DewColor, i => h.temperatureDewPoint[i], labels: true);
                    return (F("HourlyPageTitleDew", Degrees), list);
                case ChartKind.Pressure:
                    Add("HourlyPageColPressure", PressureColor, i => h.pressureMeanSeaLevel[i]);
                    return (F("HourlyPageTitlePressure", PressureUnit), list);
                default:
                    // Small chances would only clutter the bottom of the chart.
                    Add("HourlyPageLegendChance", ChanceColor, i => h.precipChance[i] >= 15 ? h.precipChance[i] : null, SeriesStyle.Bar, Axis.Right);
                    Add("HourlyPageLegendFeels", FeelsColor, i => Feels(i), SeriesStyle.Dashed);
                    Add("HourlyPageLegendTemperature", HighColor, i => h.temperature[i]);
                    return (F("HourlyPageTitleOverview", Degrees), list);
            }
        }

        private (double Min, double Max, double Step) AxisScale(List<Series> list)
        {
            var values = list.Where(s => s.Axis == Axis.Left).SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).ToList();
            if (values.Count == 0)
                return (0, 1, 1);
            if (Kind is ChartKind.Humidity or ChartKind.Clouds)
                return (0, 100, 25);
            var fromZero = Kind is ChartKind.Precip or ChartKind.Wind or ChartKind.Gusts or ChartKind.Uv or ChartKind.Visibility;
            var min = fromZero ? 0 : values.Min();
            var max = values.Max();
            if (Kind == ChartKind.Precip)
                max = Math.Max(max, _units == WetUnits.e ? 0.1 : 2);
            var range = Math.Max(max - min, Kind == ChartKind.Pressure && _units == WetUnits.e ? 0.05 : 1);
            var step = NiceStep(range / 4);
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
            v.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0", C);

        private static void AddText(Canvas canvas, string value, double x, double y, Brush brush, double size = 11)
        {
            var t = new TextBlock { Text = value, FontSize = size, Foreground = brush, IsHitTestVisible = false };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            canvas.Children.Add(t);
        }

        private static Border LegendBox(string name, Color color) =>
            new()
            {
                Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B)),
                Padding = new Thickness(8, 1, 8, 2),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    Text = name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x16, 0x24))
                }
            };

        private void DrawChart()
        {
            var canvas = ChartCanvas;
            canvas.Children.Clear();
            _dayLabels.Clear();
            _readoutLine = null;
            _readoutBox = null;
            _readoutText = null;
            LegendPanel.Children.Clear();

            var width = canvas.ActualWidth;
            var height = canvas.ActualHeight;
            if (_h == null || _count < 2 || width < 120 || height < 60)
                return;

            var (title, series) = BuildChart();
            ChartTitle.Text = title;
            if (series.Count > 1)
            {
                foreach (var s in series.AsEnumerable().Reverse())
                    LegendPanel.Children.Add(LegendBox(s.Name, s.Color));
            }

            var rightAxis = series.Any(s => s.Axis == Axis.Right);
            _plotLeft = 40;
            _plotTop = 6;
            _plotWidth = width - _plotLeft - (rightAxis ? 40 : 10);
            _plotHeight = height - _plotTop - 24;
            var bottom = _plotTop + _plotHeight;
            var step = _plotWidth / (_count - 1);
            double X(int i) => _plotLeft + i * step;

            var textBrush = new SolidColorBrush(TextColor);
            var gridBrush = new SolidColorBrush(GridColor);
            var h = _h;

            // Nights.
            var nightBrush = new SolidColorBrush(NightColor);
            for (var i = 0; i < _count;)
            {
                if (h.dayOrNight[i] != "N")
                {
                    i++;
                    continue;
                }
                var end = i;
                while (end < _count && h.dayOrNight[end] == "N")
                    end++;
                var left = Math.Max(_plotLeft, X(i) - step / 2);
                var right = Math.Min(_plotLeft + _plotWidth, X(end - 1) + step / 2);
                var night = new Rectangle { Width = Math.Max(0, right - left), Height = _plotHeight, Fill = nightBrush, IsHitTestVisible = false };
                Canvas.SetLeft(night, left);
                Canvas.SetTop(night, _plotTop);
                canvas.Children.Add(night);
                i = end;
            }

            // The left axis.
            var (min, max, axisStep) = AxisScale(series);
            double Y(double v) => bottom - (v - min) / (max - min) * _plotHeight;
            double YRight(double v) => bottom - v / 100 * _plotHeight;
            var unit = Kind is ChartKind.Overview or ChartKind.Temperature or ChartKind.FeelsLike or ChartKind.DewPoint ? "\u00B0" : "";
            var axisFormatStep = Kind == ChartKind.Pressure && _units == WetUnits.e ? Math.Min(axisStep, 0.05) : axisStep;
            if (Kind == ChartKind.Precip && _units == WetUnits.e)
                axisFormatStep = Math.Min(axisStep, 0.05);
            for (var v = min; v <= max + axisStep / 1000; v += axisStep)
            {
                var y = Y(v);
                canvas.Children.Add(new Line { X1 = _plotLeft, X2 = _plotLeft + _plotWidth, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });
                var label = FormatAxis(v, axisFormatStep) + unit;
                AddText(canvas, label, Math.Max(0, _plotLeft - 6 - label.Length * 6.2), y - 8, textBrush);
            }
            if (rightAxis)
            {
                foreach (var v in new[] { 0, 50, 100 })
                    AddText(canvas, v.ToString(C) + "%", _plotLeft + _plotWidth + 6, YRight(v) - 8, textBrush);
            }

            // The days: a line at each midnight and a label below the plot.
            for (var d = 0; d < _days.Count; d++)
            {
                var day = _days[d];
                var left = d == 0 ? _plotLeft : X(day.Start) - step / 2;
                var right = d == _days.Count - 1 ? _plotLeft + _plotWidth : X(day.Start + day.Count) - step / 2;
                if (d > 0)
                    canvas.Children.Add(new Line { X1 = left, X2 = left, Y1 = _plotTop, Y2 = bottom + 4, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });

                var text = new TextBlock { Text = day.Date.ToString("ddd d", C), FontSize = 11, Foreground = textBrush };
                var box = new Border { Child = text, Padding = new Thickness(8, 0, 8, 1), CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
                box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var boxWidth = box.DesiredSize.Width;
                if (right - left < boxWidth + 2)
                    box.Visibility = Visibility.Collapsed;
                Canvas.SetLeft(box, (left + right) / 2 - boxWidth / 2);
                Canvas.SetTop(box, bottom + 5);
                canvas.Children.Add(box);
                _dayLabels.Add((box, text));
            }
            StyleDayLabels();

            // The values.
            var any = false;
            var barCount = series.Count(s => s.Style == SeriesStyle.Bar && s.Axis == Axis.Left);
            var barIndex = 0;
            foreach (var s in series)
            {
                var brush = new SolidColorBrush(s.Color);
                Func<double, double> y = s.Axis == Axis.Right ? YRight : Y;
                if (s.Style == SeriesStyle.Bar)
                {
                    // Bars of two series on the same axis sit side by side.
                    var sideBySide = s.Axis == Axis.Left && barCount > 1;
                    var barWidth = Math.Max(1, sideBySide ? step * 0.45 : step * 0.8);
                    var offset = sideBySide ? (barIndex == 0 ? -barWidth : 0) : -barWidth / 2;
                    if (s.Axis == Axis.Left)
                        barIndex++;
                    for (var i = 0; i < _count; i++)
                    {
                        if (s.Values[i] is not { } value || value <= 0)
                            continue;
                        any = true;
                        var top = y(value);
                        var bar = new Rectangle { Width = barWidth, Height = Math.Max(1, bottom - top), Fill = brush, IsHitTestVisible = false };
                        Canvas.SetLeft(bar, X(i) + offset);
                        Canvas.SetTop(bar, Math.Min(top, bottom - 1));
                        canvas.Children.Add(bar);
                    }
                    continue;
                }

                Polyline? line = null;
                for (var i = 0; i < _count; i++)
                {
                    if (s.Values[i] is not { } value)
                    {
                        line = null;
                        continue;
                    }
                    any = true;
                    if (line == null)
                    {
                        line = new Polyline
                        {
                            Stroke = brush, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
                            StrokeThickness = s.Style == SeriesStyle.Line ? 2 : 1.3
                        };
                        if (s.Style == SeriesStyle.Dashed)
                            line.StrokeDashArray = new DoubleCollection { 3, 3 };
                        canvas.Children.Add(line);

                        // A value with no neighbor would not show as a line.
                        if (i + 1 >= _count || s.Values[i + 1] == null)
                        {
                            var dot = new Rectangle { Width = 4, Height = 4, Fill = brush, IsHitTestVisible = false };
                            Canvas.SetLeft(dot, X(i) - 2);
                            Canvas.SetTop(dot, y(value) - 2);
                            canvas.Children.Add(dot);
                        }
                    }
                    line.Points.Add(new Point(X(i), y(value)));
                }

                if (s.Labels)
                    LabelDays(canvas, s, X, y, brush, step);
            }

            ChartEmptyText.Text = any ? "" : L(Kind == ChartKind.Gusts ? "HourlyPageNoGusts" : "HourlyPageNoData");
            ChartEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

            // The readout: a line at the hour under the pointer and its values.
            _readoutLine = new Line
            {
                Y1 = _plotTop, Y2 = bottom, Stroke = new SolidColorBrush(ReadoutLineColor), StrokeThickness = 1,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            canvas.Children.Add(_readoutLine);
            _readoutText = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Colors.White) };
            _readoutBox = new Border
            {
                Child = _readoutText, Background = new SolidColorBrush(Color.FromArgb(240, 0x1D, 0x24, 0x30)),
                BorderBrush = gridBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 3), Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            canvas.Children.Add(_readoutBox);
        }

        // The highest and the lowest value of each day, when the day is wide enough for both.
        private void LabelDays(Canvas canvas, Series s, Func<int, double> x, Func<double, double> y, Brush brush, double step)
        {
            foreach (var day in _days)
            {
                if (day.Count * step < 70 || day.Count < 12)
                    continue;
                int? hi = null, lo = null;
                for (var i = day.Start; i < day.Start + day.Count; i++)
                {
                    if (s.Values[i] is not { } value)
                        continue;
                    if (hi == null || value > s.Values[hi.Value]!.Value) hi = i;
                    if (lo == null || value < s.Values[lo.Value]!.Value) lo = i;
                }
                if (hi == null || lo == null)
                    continue;
                void Label(int i, double dy)
                {
                    var t = new TextBlock
                    {
                        Text = s.Values[i]!.Value.ToString("0", C), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                        Foreground = brush, IsHitTestVisible = false
                    };
                    Canvas.SetLeft(t, x(i) - 7);
                    Canvas.SetTop(t, Math.Max(_plotTop - 4, Math.Min(_plotTop + _plotHeight - 14, y(s.Values[i]!.Value) + dy)));
                    canvas.Children.Add(t);
                }
                Label(hi.Value, -16);
                if (lo.Value != hi.Value)
                    Label(lo.Value, 2);
            }
        }

        private int HourAt(double x) =>
            _count < 2 || _plotWidth <= 0 ? 0 : Math.Clamp((int) Math.Round((x - _plotLeft) / _plotWidth * (_count - 1)), 0, _count - 1);

        private string ReadoutText(int i)
        {
            var h = _h!;
            var when = h.validTimeLocal[i].DateTime.ToString("ddd", C) + " " + HourText(i) + ": ";
            var temp = h.temperature[i].ToString(C) + "\u00B0";
            var chance = h.precipChance[i].ToString(C) + "%";
            return when + Kind switch
            {
                ChartKind.Temperature => temp + ", " + h.wxPhraseLong[i],
                ChartKind.FeelsLike => L("HourlyPageDetFeels") + " " + Feels(i).ToString(C) + "\u00B0, " + L("HourlyPageLegendTemperature") + " " + temp,
                ChartKind.Precip => chance + " " + TypeText(i) + ", " + RainText(i) + (h.qpfSnow[i] > 0 ? ", " + F("HourlyPageAmountSnow", SnowText(i)) : "") + ", " + h.wxPhraseLong[i],
                ChartKind.Wind => WindText(i) + (h.windGust[i] is { } g ? ", " + F("HourlyPageGusts", g) : ""),
                ChartKind.Gusts => h.windGust[i] is { } gust ? F("HourlyPageGusts", gust) + " " + WindUnit : L("HourlyPageNone"),
                ChartKind.Humidity => L("HourlyPageColHumidity") + " " + h.relativeHumidity[i].ToString(C) + "%",
                ChartKind.Uv => L("HourlyPageDetUv") + " " + h.uvIndex[i].ToString(C) + (string.IsNullOrEmpty(h.uvDescription[i]) ? "" : " (" + h.uvDescription[i] + ")"),
                ChartKind.Clouds => L("HourlyPageDetClouds") + " " + h.cloudCover[i].ToString(C) + "%, " + h.wxPhraseLong[i],
                ChartKind.Visibility => L("HourlyPageDetVis") + " " + h.visibility[i].ToString("0.#", C) + " " + VisibilityUnit,
                ChartKind.DewPoint => L("HourlyPageDetDew") + " " + h.temperatureDewPoint[i].ToString(C) + "\u00B0, " + L("HourlyPageLegendTemperature") + " " + temp,
                ChartKind.Pressure => h.pressureMeanSeaLevel[i].ToString(PressureFormat, C) + " " + PressureUnit,
                _ => temp + ", " + F("HourlyPageReadoutFeels", Feels(i)) + ", " + h.wxPhraseLong[i] + ", " + chance
            };
        }

        private void ChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_h == null || _readoutLine == null || _readoutBox == null || _readoutText == null)
                return;
            var position = e.GetCurrentPoint(ChartCanvas).Position;
            if (position.X < _plotLeft - 4 || position.X > _plotLeft + _plotWidth + 4 || position.Y < _plotTop || position.Y > _plotTop + _plotHeight)
            {
                HideReadout();
                return;
            }

            var i = HourAt(position.X);
            var x = _plotLeft + i * _plotWidth / (_count - 1);
            _readoutLine.X1 = x;
            _readoutLine.X2 = x;
            _readoutLine.Visibility = Visibility.Visible;
            _readoutText.Text = ReadoutText(i);
            _readoutBox.Visibility = Visibility.Visible;
            _readoutBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var boxWidth = _readoutBox.DesiredSize.Width;
            var left = x + 12;
            if (left + boxWidth > ChartCanvas.ActualWidth)
                left = x - 12 - boxWidth;
            Canvas.SetLeft(_readoutBox, Math.Max(0, left));
            Canvas.SetTop(_readoutBox, _plotTop + 2);
        }

        private void ChartCanvas_PointerExited(object sender, PointerRoutedEventArgs e) => HideReadout();

        private void HideReadout()
        {
            if (_readoutLine != null)
                _readoutLine.Visibility = Visibility.Collapsed;
            if (_readoutBox != null)
                _readoutBox.Visibility = Visibility.Collapsed;
        }

        // A click in the plot scrolls the list to that hour. A click on a day label below the plot
        // scrolls to the heading of that day.
        private void ChartCanvas_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_h == null || _count == 0 || _items.Count == 0)
                return;
            var position = e.GetPosition(ChartCanvas);
            if (position.X < _plotLeft - 4 || position.X > _plotLeft + _plotWidth + 4)
                return;
            var hour = HourAt(position.X);
            var code = position.Y > _plotTop + _plotHeight ? -(DayOf(hour) + 1) : hour;
            var row = _rows.IndexOf(code);
            if (row >= 0 && row < _items.Count)
                HoursList.ScrollIntoView(_items[row], ScrollIntoViewAlignment.Leading);
        }
    }
}
