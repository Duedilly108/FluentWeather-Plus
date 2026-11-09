using CommunityToolkit.WinUI.Controls;
using FluentWeather.Adapters;
using FluentWeather.Controls;
using FluentWeather.Dialogs;
using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Services;
using FluentWeather.Utils;
using FluentWeather.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;
using Media = Windows.UI.Xaml.Media;
using Shapes = Windows.UI.Xaml.Shapes;

namespace FluentWeather.Views
{
    [GeneratedBindableCustomPropertyAttribute]
    public sealed partial class MainPage : Page
    {
        public MainPageViewModel MainPageViewModel { get; } = new();

        public NavigationViewViewModel NavigationViewViewModel { get; } = new();

        public AcrylicEffectsService AcrylicEffectsService { get; } = Singleton<AcrylicEffectsService>.Instance;

        private readonly AppViewModel _appViewModel = AppViewModelHolder.GetViewModel();

        private static readonly HttpClient SharedClient = ApiUtils.CreateClient("https://api.weather.com/");

        // Local build: the language chosen in the app settings, otherwise the Windows display language.
        private static readonly string SystemLanguage = !string.IsNullOrEmpty(Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride)
            ? Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride
            : Windows.System.UserProfile.GlobalizationPreferences.Languages[0];

        private RootV3Response? _lastApiData;

        private Button _lastSelectedDayButton;

        private readonly DispatcherTimer _refreshTimer = new();

        // --- Saved locations ---

        // Weather data younger than this is reused when switching between saved locations.
        private static readonly TimeSpan LocationCacheMaxAge = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, RootV3Response Data)> _weatherCache = new();

        // Incremented for every load. A response is only shown if its id is still the latest,
        // so a slow response for an old location cannot overwrite a newer one.
        private int _loadRequestId;

        // The location whose data is on screen. Used by the pin button.
        private string? _displayedPlaceId;
        private string _displayedPlaceName = string.Empty;
        private string _displayedPlaceFullName = string.Empty;

        // The location the user selected last. Used to highlight a chip.
        private string? _activePlaceId;

        private IReadOnlyList<SavedLocation> _savedLocations = [];

        public int SettingsUnitsSelectedIndex
        {
            get
            {
                return Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>("selectedUnits")).Result;
            }
        }

        public int SettingsTimeFormatSelectedIndex
        {
            get
            {
                //run in background to avoid a deadlock/ui freeze
                return Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<bool>("is12HourFormat")).Result ? 1 : 0;
            }
        }

        public int SettingsUiScaleSelectedIndex => Math.Max(0, Array.IndexOf(UiScaleSteps, _uiScalePercent));

        public string UiScaleText => _uiScalePercent + "%";

        public int SettingsBackgroundContrastSelectedIndex => _backgroundContrast;

        public double SettingsCardTint => _cardTint;

        public int SettingsHourlyRangeSelectedIndex => Math.Max(0, Array.IndexOf(HourlyRangeSteps, _hourlyRangeHours));

        // Local build: language choice. Entry 0 is "Windows default", then every language in the package.
        public List<string> LanguageChoices => GetLanguageTags().Select(tag => tag.Length == 0
            ? "LanguageDefaultItem".GetLocalized()
            : GetLanguageName(tag)).ToList();

        public string CurrentLanguageName => LanguageChoices[SettingsLanguageSelectedIndex] + "  \u2026";

        public int SettingsLanguageSelectedIndex
        {
            get
            {
                var current = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride ?? string.Empty;
                var index = GetLanguageTags().FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? 0 : index;
            }
        }

        public bool BackgroundImageToggleStatus
        {
            get
            {
                //run in background to avoid a deadlock/ui freeze
                return Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<bool>("backgroundImageEnabled", true)).Result;
            }
        }

        public List<string> AutoRefreshStepsValues => AutoRefreshSteps.Values.ToList();

        public Dictionary<int, string> AutoRefreshSteps => new()
        {
            {0, "Off"},
            {15, "15 min"},
            {30, "30 min"},
            {60, "1 hour"}
        };

        public int AutoRefreshPeriodSelectedIndex
        {
            get
            {
                //run in background to avoid a deadlock/ui freeze
                var savedVal = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>("autoRefreshDataMinutes")).Result;
                var keys = AutoRefreshSteps.Keys.ToList();

                // The shortest interval is 15 minutes. An older saved value of 5 or 10 minutes uses 15.
                if (!keys.Contains(savedVal))
                    savedVal = savedVal <= 0 ? 0 : keys.Where(k => k > 0).OrderBy(k => Math.Abs(k - savedVal)).First();

                return keys.IndexOf(savedVal);
            }
        }

        private async void VersionText_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WhatsNewDialog();
            await dialog.ShowAsync();
        }

        public MainPage()
        {
            //use the EntranceNavigationTransition when showing the page at startup (pageload)
            Transitions = [new EntranceThemeTransition()];

            InitializeComponent();

            DataContext = this; //DataContext = ViewModel;
            Initialize();
        }

        private void Initialize()
        {
            // Hide default title bar
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;

            AppTitleTextBlock.Text = Package.Current.DisplayName;
            Window.Current.SetTitleBar(TitleBarGrid);

            //remove the solid-colored backgrounds behind the caption controls and system back button
            ApplicationViewTitleBar titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            //NavigationViewViewModel.Initialize(contentFrame, navigationView, KeyboardAccelerators);

            //pagina di default
            //NavigationService.Navigate(typeof(Views.DashWet));

            MainPageViewModel.AppVersionText = GetVersionDescription();
            MainPageViewModel.PlusVersionText = GetPlusVersionDescription();
            ToolTipService.SetToolTip(TitleIconHost, Package.Current.DisplayName + " " +
                                                     MainPageViewModel.PlusVersionText + "\n" +
                                                     GetFullVersionDescription());

            InitializeStoryboards();

            _appViewModel.UpdateUiAction += async () => { await Task.Run(LoadApiData); };
            KeyDown += MainPage_KeyDown;

            AutoSuggestBoxMain.TextChanged += AutoSuggestBox_TextChanged;

            _appViewModel.UpdateUi();

            // Initialize the refresh timer
            _refreshTimer.Tick += RefreshTimerTickEvent;
            UpdateRefreshTimerSettings();

            NetworkInformation.NetworkStatusChanged += sender =>
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                var level = profile?.GetNetworkConnectivityLevel();
                if (level == NetworkConnectivityLevel.InternetAccess)
                {
                    _appViewModel.UpdateUi();
                }
            };

            // Chips are created lazily by the ItemsRepeater. Set their checked state when each one is created.
            SavedLocationsRepeater.ElementPrepared += (_, args) =>
            {
                if (args.Element is ToggleButton chip && args.Index >= 0 && args.Index < _renderedLocations.Count)
                    chip.IsChecked = _renderedLocations[args.Index].PlaceId == _activePlaceId;
            };

            // Local build: the day buttons are created lazily too. Give each one the card tint.
            RepeaterDays.ElementPrepared += (_, args) =>
            {
                if (args.Element is Button dayButton)
                    ApplyCardTintToDayButton(dayButton);
            };

            InitializeSavedLocationShortcuts();
            InitializeUiScale();
            InitializeBackgroundContrast();
            InitializeRadar();
            InitializeHourlyRange();
            InitializeTitleBarLayout();
            _ = ApplyCompactWindowSizeOnceAsync();
            _ = ReloadSavedLocationsAsync();

            // A protocol link (from the tray helper) asks to show a saved location while the app is open.
            _appViewModel.SwitchLocationRequested += async placeId => await SwitchToLocationAsync(placeId);
        }

        private async void RefreshTimerTickEvent(object? sender, object e)
        {
            await LoadApiData();
        }

        // The FluentWeather-Plus version is the fourth part of the package version: 2.1.0.28 is v28.
        private static string GetPlusVersionDescription()
        {
            return $"v{Package.Current.Id.Version.Revision}";
        }

        private static string GetFullVersionDescription()
        {
            var version = Package.Current.Id.Version;
            return $"Build {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        private static string GetVersionDescription()
        {
            var package = Package.Current;
            var packageId = package.Id;
            var version = packageId.Version;

            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        internal static async void AutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            sender.ShowHideCustomHeader(true);
            sender.ShowHideCustomHeader(false, AutoSuggestBoxClassExtensions.AutoSuggestBoxHeaderType.NetworkError); //TODO: remove this line and clear all messages automatically in BetterAutosuggestBox.cs

            // Since selecting an item will also change the text,
            // only listen to changes caused by user entering text.
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput
                && !string.IsNullOrEmpty(sender.Text))
            {
                try
                {
                    var response = await ApiUtils.GetWithRetryAsync(SharedClient, "v3/location/searchflat?query=" + sender.Text +
                                                               "&language=" +
                                                               SystemLanguage +
                                                               "&apiKey=793db2b6128c4bc2bdb2b6128c0bc230&format=json");
                    //&locationType=city (x solo citta)

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonResponse = await response.Content.ReadAsStringAsync();
                        var locResponse = System.Text.Json.JsonSerializer.Deserialize<SearchLocationResponse>(jsonResponse, FluentWeatherJsonContext.Default.SearchLocationResponse);
                        var finalitems = locResponse?.location.Select(x => x).ToList();
                        sender.ItemsSource = finalitems;
                    }
                }
                catch (Exception e)
                {
                    if (e is HttpRequestException or TaskCanceledException)
                    {
                        sender.ShowHideCustomHeader(true, AutoSuggestBoxClassExtensions.AutoSuggestBoxHeaderType.NetworkError);
                        return;
                    }
                    throw;
                }
            }

            sender.ShowHideCustomHeader(false);
        }

        private async void AutoSuggestBox_SuggestionChosen(AutoSuggestBox sender,
            AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            var selectedPlaceId = ((SearchedLocation) args.SelectedItem).placeId;
            await SwitchToLocationAsync(selectedPlaceId);
        }

        private bool IsLatestLoad(int requestId) => requestId == Volatile.Read(ref _loadRequestId);

        private async Task LoadApiData()
        {
            var requestId = Interlocked.Increment(ref _loadRequestId);

            await CoreApplication.MainView.Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
                {
                    RefreshButton.Visibility = Visibility.Collapsed;
                    MainPageViewModel.IsLoadingData = true;
                    VariousUtils.CloseOpenContentDialogs("WeatherDataUpdateFailedDialog");
                }
            );

            try
            {
                var lastPlaceId = await ApplicationData.Current.LocalSettings.ReadAsync<string>("lastPlaceId");

                if (lastPlaceId == null) return;

                var response = await ApiUtils.GetFullData(lastPlaceId);
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var apiData = System.Text.Json.JsonSerializer.Deserialize(jsonResponse, FluentWeatherJsonContext.Default.RootV3Response);

                if (apiData != null)
                    _weatherCache[lastPlaceId] = (DateTimeOffset.UtcNow, apiData);

                // The user switched to another location while this request was running.
                if (!IsLatestLoad(requestId)) return;

                _lastApiData = apiData;

                if (_lastApiData != null)
                {
                    Singleton<LiveTileService>.Instance.UpdateWeatherMainTile(_lastApiData);
                    // Keep the tray data for all saved locations current (at most every 10 minutes).
                    _ = TrayWeatherFile.RefreshAllSavedAsync(false);
                }

                await CoreApplication.MainView.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal, () =>
                    {
                        if (!IsLatestLoad(requestId)) return;

                        if (_lastApiData != null)
                            UpdateUi(_lastApiData, lastPlaceId);
                        MainInnerContentGrid.Visibility = Visibility.Visible;
                    }
                );
            }
            catch (Exception e)
            {
                if (!IsLatestLoad(requestId)) return;

                if (e is HttpRequestException or TaskCanceledException)
                {
                    await CoreApplication.MainView.Dispatcher.RunAsync(
                        CoreDispatcherPriority.Normal,
                        async void () =>
                        {
                            ContentDialog noWifiDialog = new()
                            {
                                Title = "Weather data update failed",
                                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                                Content = "We couldn't update weather data. Verify your internet connection.\n\n" + e.InnerException?.Message,
                                CloseButtonText = "OK",
                                Tag = "WeatherDataUpdateFailedDialog"
                            };
                        
                            try
                            {
                                await noWifiDialog.ShowAsync();
                            }
                            catch (Exception)
                            {
                            }
                        }
                    );

                    return;
                }
                throw;
            }
            finally
            {
                // Only the latest load controls the spinner. An older load must not hide it early.
                if (IsLatestLoad(requestId))
                {
                    await CoreApplication.MainView.Dispatcher.RunAsync(
                        CoreDispatcherPriority.Normal, () =>
                        {
                            RefreshButton.Visibility = Visibility.Visible;
                            MainPageViewModel.IsLoadingData = false;
                        }
                    );
                }
            }
        }


        private Storyboard _storyboard1;
        private Storyboard _storyboard2;
        private bool _isImage1Active;

        private void CrossfadeToImage(Uri? newImageUri)
        {
            BitmapImage newImage = newImageUri == null ? new() : new(newImageUri);

            if (_isImage1Active)
                Image2.Source = newImage;
            else
                Image1.Source = newImage;

            if (newImageUri == null)
                ResetStartStoryboard();
            else
            {
                //the imageopened event is fired only when the image is shown in some way :( so we need to set the image source before
                //this ensures the animation is played only when the image is loaded
                newImage.ImageOpened += (sender, args) => { ResetStartStoryboard(); };
            }

            return;

            void ResetStartStoryboard()
            {
                if (_isImage1Active)
                {
                    _storyboard1.Stop();
                    _storyboard2.Begin();
                }
                else
                {
                    _storyboard2.Stop();
                    _storyboard1.Begin();
                }

                _isImage1Active = !_isImage1Active;
            }
        }

        private void InitializeStoryboards()
        {
            _storyboard1 = CreateCrossfadeStoryboard(Image1, Image2);
            _storyboard2 = CreateCrossfadeStoryboard(Image2, Image1);
            _isImage1Active = true;
        }

        private Storyboard CreateCrossfadeStoryboard(Image fadeInImage, Image fadeOutImage)
        {
            DoubleAnimation fadeInAnimation = new()
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromSeconds(1),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fadeInAnimation, fadeInImage);
            Storyboard.SetTargetProperty(fadeInAnimation, "Opacity");

            DoubleAnimation fadeOutAnimation = new()
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromSeconds(1),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fadeOutAnimation, fadeOutImage);
            Storyboard.SetTargetProperty(fadeOutAnimation, "Opacity");

            Storyboard storyboard = new();
            storyboard.Children.Add(fadeInAnimation);
            storyboard.Children.Add(fadeOutAnimation);

            return storyboard;
        }

        private async void UpdateUi(RootV3Response rootV3Response, string placeId)
        {
            CrossfadeToImageApiData(rootV3Response);

            var newIconUri = new Uri("ms-appx:///Assets/weticons/" + rootV3Response.v3wxobservationscurrent.iconCode + ".svg");

            //get svgimagesource object from image source
            var svgImageSource = (SvgImageSource) MainIcon.Source;
            svgImageSource.UriSource = newIconUri;
            MainIcon.Source = svgImageSource;

            //update chips
            WetUnits currentUnits = await VariousUtils.GetUnitsCode();

            // Local build: the date in the Windows date format, and the time in the app's 12 or 24 hour setting.
            var updatedDate = new Windows.Globalization.DateTimeFormatting.DateTimeFormatter("shortdate").Format(DateTimeOffset.Now);
            var updatedTime = await VariousUtils.GetTimeBasedOnUserSettings(DateTime.Now);
            UpdatedOnText.Text = "UpdatedOnText".GetLocalized() + updatedDate + " " + updatedTime + " " + TimeZoneInfo.Local.Id;

            //join the variables displayName, city, adminDistrict, country together in a single string with a comma between each variable (some may be empty) and remove duplicate names
            string placeName = string.Join(", ",
                new[]
                {
                    rootV3Response.v3locationpoint.LocationV3.displayName,
                    rootV3Response.v3locationpoint.LocationV3.city,
                    rootV3Response.v3locationpoint.LocationV3.adminDistrict,
                    rootV3Response.v3locationpoint.LocationV3.country
                }.Where(s => !string.IsNullOrEmpty(s)).Distinct());

            PlaceText.Text = placeName;

            var locationV3 = rootV3Response.v3locationpoint.LocationV3;
            _displayedPlaceId = placeId;
            _activePlaceId = placeId;
            _displayedPlaceFullName = placeName;
            _displayedPlaceName = new[] { locationV3.displayName, locationV3.city, placeName }
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? placeId;
            UpdateActiveChip();
            UpdateSaveLocationButton();
            UpdateRadar(locationV3.latitude, locationV3.longitude, locationV3.countryCode);
            _ = UpdateAirQualityAsync(locationV3.latitude, locationV3.longitude, locationV3.countryCode, placeId);

            MainPhraseText.Text = rootV3Response.v3wxobservationscurrent.cloudCoverPhrase;

            CurrentTempText.Text = rootV3Response.v3wxobservationscurrent.temperature + "°" +
                                   MeasureUnitUtils.GetTemperatureUnits(currentUnits);

            FeelsLikeText.Text = "FeelsLikeText".GetLocalized() + " " +
                                 rootV3Response.v3wxobservationscurrent.temperatureFeelsLike + "°" +
                                 MeasureUnitUtils.GetTemperatureUnits(currentUnits);

            WindChipControl.Value = rootV3Response.v3wxobservationscurrent.windSpeed + " " +
                                    MeasureUnitUtils.GetWindSpeedUnits(currentUnits) + " " +
                                    rootV3Response.v3wxobservationscurrent.windDirectionCardinal;

            //CloudCoverChipControl.Value = rootV3Response.v3wxobservationscurrent.phrase;

            HumidityChipControl.Value = rootV3Response.v3wxobservationscurrent.relativeHumidity + "%";

            PressureChipControl.Value = rootV3Response.v3wxobservationscurrent.pressureMeanSeaLevel + " " +
                                        MeasureUnitUtils.GetPressureUnits(currentUnits);

            VisibilityChipControl.Value = rootV3Response.v3wxobservationscurrent.visibility + " " +
                                          MeasureUnitUtils.GetVisibilityUnits(currentUnits);

            DewPointChipControl.Value = rootV3Response.v3wxobservationscurrent.temperatureDewPoint + "°" +
                                        MeasureUnitUtils.GetTemperatureUnits(currentUnits);

            UVIndexChipControl.Value = rootV3Response.v3wxobservationscurrent.uvIndex + " (" +
                                       rootV3Response.v3wxobservationscurrent.uvDescription + ")";

            //update days repeater itemssource with creating for every day a new DayButtonAdapter class
            List<DayButtonAdapter> dayButtonAdapters = [];

            int i = 0;
            foreach (var day in rootV3Response.v3wxforecastdaily10day.dayOfWeek)
            {
                dayButtonAdapters.Add(new DayButtonAdapter(rootV3Response.v3wxforecastdaily10day, i));
                i++;
            }

            RepeaterDays.ItemsSource = dayButtonAdapters;

            //select first day button
            EmulateDayButtonClick(0);

            // Local build: record today for "Past days" and show the recorded days left of today.
            _ = UpdatePastDaysAsync(rootV3Response, placeId);
        }

        private void CrossfadeToImageApiData(RootV3Response? rootV3Response = null)
        {
            if (!BackgroundImageToggleStatus)
                return;

            rootV3Response ??= _lastApiData;

            if (rootV3Response == null)
                return;

            var newImageUri = new Uri("ms-appx:///Assets/bgs/" +
                                      IconsDictionary.IconCodeToBackgroundImageNameDictionary[rootV3Response.v3wxobservationscurrent.iconCode.ToString()] + ".jpg");

            CrossfadeToImage(newImageUri);
        }

        private async void LoadHourlyData(DateTimeOffset dayToLoad)
        {
            if (_lastApiData == null) return;

            _lastHourlyDay = dayToLoad;

            List<HourDataAdapter> hourlyDataAdapters = [];

            WetUnits currentUnits = await VariousUtils.GetUnitsCode();

            int i = 0;

            var firstDate = _lastApiData.v3wxforecasthourly10day.validTimeLocal[0];

            //subtract daytoload to firstdate to get the index of the first day to load
            int daysDiff = dayToLoad.Date.Subtract(firstDate.Date).Days;

            // Local build: a fixed number of hours. Today starts at the current hour, other days at midnight.
            if (_hourlyRangeHours > 0)
            {
                var times = _lastApiData.v3wxforecasthourly10day.validTimeLocal;
                var start = daysDiff <= 0 ? 0 : times.FindIndex(x => x.Date == firstDate.Date.AddDays(daysDiff));
                if (start < 0) start = 0;

                var end = Math.Min(times.Count, start + _hourlyRangeHours);
                for (var h = start; h < end; h++)
                {
                    var showDay = h == start || times[h].Hour == 0;
                    hourlyDataAdapters.Add(new HourDataAdapter(_lastApiData.v3wxforecasthourly10day, h, currentUnits, showDay));
                }

                HourlyListview.ItemsSource = hourlyDataAdapters;
                return;
            }

            if (daysDiff == -1 ||
                (daysDiff == 0 &&
                 _lastApiData.v3wxforecasthourly10day.validTimeLocal[0].Hour > 7)) //this means that we are still in a tonight/today state and we add hours only until 7AM
            {
                int h = 0;
                while (_lastApiData.v3wxforecasthourly10day.validTimeLocal[h].Hour != 7)
                {
                    hourlyDataAdapters.Add(new HourDataAdapter(_lastApiData.v3wxforecasthourly10day, h, currentUnits));
                    h++;
                }
            }
            else
            {
                foreach (var date in _lastApiData.v3wxforecasthourly10day.validTimeLocal)
                {
                    //the .Date property is used to compare only the date part of the datetime without the time (no hours, minutes, seconds)
                    if (date.Date == firstDate.Date.AddDays(daysDiff))
                    {
                        hourlyDataAdapters.Add(new HourDataAdapter(_lastApiData.v3wxforecasthourly10day, i, currentUnits));
                    }

                    i++;
                }
            }

            HourlyListview.ItemsSource = hourlyDataAdapters;
        }

        private async void LoadInsightsData(DateTimeOffset dayToLoad)
        {
            if (_lastApiData == null) return;

            /*var indexOfDay =
                lastApiData.v2idxDriveDaypart10days.drivingDifficultyIndex12hour.fcstValidLocal.IndexOf(dayToLoad.Date);*/

            var indexOfDayInsights = _lastApiData.v2idxDriveDaypart10days.drivingDifficultyIndex12hour.fcstValidLocal.FindIndex(x => x.Date == dayToLoad.Date);

            RunningInsight.Insight = new Insight
            {
                Title = "RunningInsightTitle".GetLocalized(),
                Value = _lastApiData.v2idxRunDaypart10days.RunWeatherIndexDaypart.longRunWeatherIndex[indexOfDayInsights],
                Description = _lastApiData.v2idxRunDaypart10days.RunWeatherIndexDaypart.longRunWeatherCategory[indexOfDayInsights],
                Levels = InsightLevels.RunningLevels,
                IconName = "running"
            };

            DrivingInsight.Insight = new Insight
            {
                Title = "DrivingInsightTitle".GetLocalized(),
                Value = _lastApiData.v2idxDriveDaypart10days.drivingDifficultyIndex12hour.drivingDifficultyIndex[indexOfDayInsights],
                Description = _lastApiData.v2idxDriveDaypart10days.drivingDifficultyIndex12hour.drivingDifficultyCategory[indexOfDayInsights],
                Levels = InsightLevels.DrivingLevels,
                IconName = "driving"
            };

            /*PollenInsight.Insight = new Insight() {Title = "Pollen",
                Value = rootV3Response.v2idxPollenDaypart10days.PollenForecastDaypart.ind[0],
                Description = rootV3Response.v2idxRunDaypart10days.RunWeatherIndexDaypart.longRunWeatherCategory[0],
                Levels = InsightLevels.RunningLevels};*/

            DryskinInsight.Insight = new Insight
            {
                Title = "DrySkinInsightTitle".GetLocalized(),
                Value = _lastApiData.V2IdxDrySkinDaypart10days.DrySkinIndexDaypart.drySkinIndex[indexOfDayInsights],
                Description = _lastApiData.V2IdxDrySkinDaypart10days.DrySkinIndexDaypart.drySkinCategory[indexOfDayInsights],
                Levels = InsightLevels.DrySkinLevels,
                IconName = "dry"
            };

            WateringInsight.Insight = new Insight
            {
                Title = "WateringNeedInsightTitle".GetLocalized(),
                Value = _lastApiData.V2IdxWateringDaypart10days.WateringNeedsIndexDaypart.wateringNeedsIndex[indexOfDayInsights],
                Description = _lastApiData.V2IdxWateringDaypart10days.WateringNeedsIndexDaypart.wateringNeedsCategory[indexOfDayInsights],
                Levels = InsightLevels.WateringLevels,
                IconName = "watering"
            };

            var indexOfDayDailyData = _lastApiData.v3wxforecastdaily10day.validTimeLocal.FindIndex(x => x.Date == dayToLoad.Date);

            //day summary
            var daySummaryString = _lastApiData.v3wxforecastdaily10day.daypart[0].narrative[indexOfDayDailyData * 2];

            DaySummaryText.Text = daySummaryString ?? "--";
            NightSummaryText.Text = _lastApiData.v3wxforecastdaily10day.daypart[0].narrative[indexOfDayDailyData * 2 + 1];

            //sunset and sunrise
            var sunriseTime = _lastApiData.v3wxforecastdaily10day.sunriseTimeLocal[indexOfDayDailyData];
            SunriseTimeText.Text = sunriseTime == null
                ? "--"
                : await VariousUtils.GetTimeBasedOnUserSettings(sunriseTime.Value.DateTime);

            var sunsetTime = _lastApiData.v3wxforecastdaily10day.sunsetTimeLocal[indexOfDayDailyData];
            SunsetTimeText.Text = sunsetTime == null
                ? "--"
                : await VariousUtils.GetTimeBasedOnUserSettings(sunsetTime.Value.DateTime);

            //moonrise and moonset
            var moonriseTime = _lastApiData.v3wxforecastdaily10day.moonriseTimeLocal[indexOfDayDailyData];
            MoonriseTimeText.Text = moonriseTime == null
                ? "--"
                : await VariousUtils.GetTimeBasedOnUserSettings(moonriseTime.Value.DateTime);

            var moonsetTime = _lastApiData.v3wxforecastdaily10day.moonsetTimeLocal[indexOfDayDailyData];
            MoonsetTimeText.Text = moonsetTime == null
                ? "--"
                : await VariousUtils.GetTimeBasedOnUserSettings(moonsetTime.Value.DateTime);

            //lunar phase
            var lunarPhase = _lastApiData.v3wxforecastdaily10day.moonPhase[indexOfDayDailyData];
            LunarphaseText.Text = lunarPhase;

            //moon phase icon
            var moonPhaseIcon = _lastApiData.v3wxforecastdaily10day.moonPhaseCode[indexOfDayDailyData];

            var svgImageSource = new SvgImageSource(new Uri($"ms-appx:///Assets/varicons/{moonPhaseIcon}.svg"));
            LunarphaseIcon.Source = svgImageSource;
        }

        private void DayButtonClick(object sender, RoutedEventArgs e)
        {
            var button = (Button) sender;

            SetDayButtonClickedStyle(button);

            var dayButtonAdapter = (DayButtonAdapter) button.DataContext;
            LoadHourlyData(dayButtonAdapter.CurrentObject.validTimeLocal[dayButtonAdapter.ItemIndex]);
            LoadInsightsData(dayButtonAdapter.CurrentObject.validTimeLocal[dayButtonAdapter.ItemIndex]);
        }

        private void SetDayButtonClickedStyle(Button button)
        {
            // Reset the style of the previously selected button
            if (_lastSelectedDayButton != null)
            {
                //apply default button style of winui
                _lastSelectedDayButton.Style = (Style) Application.Current.Resources["DefaultButtonStyle"];
            }

            // Apply the style to the selected button
            button.Style = (Style) Resources["SelectedDayButtonStyle"];

            // Keep track of the selected button
            var previous = _lastSelectedDayButton;
            _lastSelectedDayButton = button;

            // Local build: the selected day keeps its own dark fill. The others get the card tint.
            if (previous != null && previous != button)
                ApplyCardTintToDayButton(previous);
            ApplyCardTintToDayButton(button);
        }

        private void EmulateDayButtonClick(int index)
        {
            var button = (Button) RepeaterDays.GetOrCreateElement(index);
            var ap = new ButtonAutomationPeer(button);
            var ip = ap.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            ip?.Invoke();
        }

        private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
        {
            _appViewModel.UpdateUi();
        }

        private async void UnitsSegmented_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var segmented = (Segmented) sender;

            if (!segmented.IsLoaded)
                return;

            //save selected index to local settings
            await ApplicationData.Current.LocalSettings.SaveAsync("selectedUnits", segmented.SelectedIndex);
            _appViewModel.UpdateUi();
        }

        private void AutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            // Only keeps the suggestions list open if no suggestion was chosen.
            sender.IsSuggestionListOpen = true;
        }

        private void AutoSuggestBoxMain_OnKeyUp(object sender, KeyRoutedEventArgs e)
        {
            AutoSuggestBox sender2 = (AutoSuggestBox) sender;
            sender2.IsSuggestionListOpen = true;
        }

        private async void AcrylicToggle_OnToggled(object sender, RoutedEventArgs e)
        {
            var acrylicToggle = (ToggleSwitch) sender;

            //save selected index to local settings
            await ApplicationData.Current.LocalSettings.SaveAsync("effectsEnabled", acrylicToggle.IsOn);
            await AcrylicEffectsService.SetThemeAsync(acrylicToggle.IsOn);
        }

        private void RefContainer_RefreshRequested(RefreshContainer sender, RefreshRequestedEventArgs args)
        {
            _appViewModel.UpdateUi();
        }

        private async void TimeFormatSegmented_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var segmented = (Segmented) sender;

            if (!segmented.IsLoaded)
                return;

            await ApplicationData.Current.LocalSettings.SaveAsync("is12HourFormat", segmented.SelectedIndex == 1);
            _appViewModel.UpdateUi();
        }

        private void MainPage_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            switch (args.Key)
            {
                case VirtualKey.F5:
                    _appViewModel.UpdateUi();
                    break;
            }
        }

        private async void BackgroundImageToggle_OnToggled(object sender, RoutedEventArgs e)
        {
            var backgroundImageToggle = (ToggleSwitch) sender;
            await ApplicationData.Current.LocalSettings.SaveAsync("backgroundImageEnabled", backgroundImageToggle.IsOn);

            if (backgroundImageToggle.IsOn)
                CrossfadeToImageApiData();
            else
                CrossfadeToImage(null);

            ApplyBackgroundContrast(backgroundImageToggle.IsOn);
        }

        private void AutoRefreshSegmented_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var segmented = (Segmented) sender;
            var minutes = AutoRefreshSteps.Keys.ToList();

            if (!segmented.IsLoaded || segmented.SelectedIndex < 0 || segmented.SelectedIndex >= minutes.Count)
                return;

            UpdateRefreshTimerSettings(minutes[segmented.SelectedIndex]);
        }

        private async void UpdateRefreshTimerSettings(int minutes = -1)
        {
            if (minutes == -1)
                minutes = AutoRefreshSteps.ElementAt(AutoRefreshPeriodSelectedIndex).Key;

            if (minutes > 0)
            {
                _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
                _refreshTimer.Start();
            }
            else
                _refreshTimer.Stop();

            await ApplicationData.Current.LocalSettings.SaveAsync("autoRefreshDataMinutes", minutes);
        }

        // =====================================================================
        // Saved locations: pin, switch, reorder, remove, keyboard shortcuts
        // =====================================================================

        private List<SavedLocation> _renderedLocations = [];

        private async Task ReloadSavedLocationsAsync()
        {
            _savedLocations = await SavedLocationsService.GetAllAsync();
            _activePlaceId ??= await ApplicationData.Current.LocalSettings.ReadAsync<string>("lastPlaceId");
            RenderSavedLocations();
        }

        private void RenderSavedLocations()
        {
            // Show the full name when two saved locations have the same short name (for example two "Springfield").
            var duplicateNames = _savedLocations
                .GroupBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

            _renderedLocations = _savedLocations.Select((x, i) =>
            {
                var fullName = string.IsNullOrEmpty(x.FullName) ? x.Name : x.FullName;
                var label = string.IsNullOrEmpty(x.Name) || duplicateNames.Contains(x.Name) ? fullName : x.Name;
                var shortcut = i < 9 ? string.Format("SavedLocationShortcutHint".GetLocalized(), i + 1) : string.Empty;

                return new SavedLocation
                {
                    PlaceId = x.PlaceId,
                    Name = x.Name,
                    FullName = fullName,
                    Label = label,
                    ToolTipText = string.IsNullOrEmpty(shortcut) ? fullName : fullName + "\n" + shortcut,
                    IsActive = x.PlaceId == _activePlaceId
                };
            }).ToList();

            SavedLocationsRepeater.ItemsSource = _renderedLocations;
            SavedLocationsScroll.Visibility = _renderedLocations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSavedLocationsHint();

            UpdateActiveChip();
            UpdateSaveLocationButton();
        }

        // Updates the checked state of the chips in place, so keyboard focus stays on the chip.
        private void UpdateActiveChip()
        {
            for (var i = 0; i < _renderedLocations.Count; i++)
            {
                var isActive = _renderedLocations[i].PlaceId == _activePlaceId;
                _renderedLocations[i].IsActive = isActive;

                if (SavedLocationsRepeater.TryGetElement(i) is ToggleButton chip)
                    chip.IsChecked = isActive;
            }
        }

        private const string SavedLocationsTipHiddenKey = "savedLocationsTipHidden";
        private bool? _savedLocationsTipHidden;

        // Shows how to save a location with the star button.
        private void UpdateSavedLocationsHint()
        {
            if (_savedLocationsTipHidden == null)
            {
                try
                {
                    //run in background to avoid a deadlock/ui freeze
                    _savedLocationsTipHidden = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<bool>(SavedLocationsTipHiddenKey)).Result;
                }
                catch (Exception)
                {
                    _savedLocationsTipHidden = false;
                }
            }

            var hasSaved = _renderedLocations.Count > 0;
            if (hasSaved && _savedLocationsTipHidden == true)
            {
                SavedLocationsHintPanel.Visibility = Visibility.Collapsed;
                return;
            }

            SavedLocationsHintText.Text = (hasSaved ? "SavedLocationsTip" : "SavedLocationsEmptyHint").GetLocalized();
            SavedLocationsHintCloseButton.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
            var closeText = "SavedLocationsHintClose".GetLocalized();
            ToolTipService.SetToolTip(SavedLocationsHintCloseButton, closeText);
            AutomationProperties.SetName(SavedLocationsHintCloseButton, closeText);
            SavedLocationsHintPanel.Visibility = Visibility.Visible;
        }

        private async void SavedLocationsHintCloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            _savedLocationsTipHidden = true;
            SavedLocationsHintPanel.Visibility = Visibility.Collapsed;
            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(SavedLocationsTipHiddenKey, true);
            }
            catch (Exception)
            {
                // The tip stays hidden until the app closes.
            }
        }

        private void UpdateSaveLocationButton()
        {
            var isSaved = _displayedPlaceId != null && _savedLocations.Any(x => x.PlaceId == _displayedPlaceId);

            SaveLocationIcon.Glyph = isSaved ? "" : ""; // FavoriteStarFill : FavoriteStar
            SaveLocationButton.IsEnabled = _displayedPlaceId != null;

            var tip = (isSaved ? "UnpinLocationTooltip" : "PinLocationTooltip").GetLocalized();
            ToolTipService.SetToolTip(SaveLocationButton, tip);
            AutomationProperties.SetName(SaveLocationButton, tip);
        }

        private async Task SwitchToLocationAsync(string placeId)
        {
            if (string.IsNullOrEmpty(placeId))
                return;

            if (placeId == _activePlaceId && placeId == _displayedPlaceId)
            {
                UpdateActiveChip();
                return;
            }

            await ApplicationData.Current.LocalSettings.SaveAsync("lastPlaceId", placeId);
            _activePlaceId = placeId;
            UpdateActiveChip();

            // Recent data for this location is shown at once, without a network call.
            if (_weatherCache.TryGetValue(placeId, out var cached) &&
                DateTimeOffset.UtcNow - cached.FetchedAt < LocationCacheMaxAge)
            {
                // Supersede any load that is still running for the previous location.
                Interlocked.Increment(ref _loadRequestId);

                _lastApiData = cached.Data;
                Singleton<LiveTileService>.Instance.UpdateWeatherMainTile(cached.Data);
                UpdateUi(cached.Data, placeId);

                MainInnerContentGrid.Visibility = Visibility.Visible;
                RefreshButton.Visibility = Visibility.Visible;
                MainPageViewModel.IsLoadingData = false;
                return;
            }

            _appViewModel.UpdateUi();
        }

        private async void SaveLocationButton_OnClick(object sender, RoutedEventArgs e)
        {
            var placeId = _displayedPlaceId;
            if (placeId == null)
                return;

            if (_savedLocations.Any(x => x.PlaceId == placeId))
            {
                await SavedLocationsService.RemoveAsync(placeId);
            }
            else if (!await SavedLocationsService.AddAsync(placeId, _displayedPlaceName, _displayedPlaceFullName))
            {
                await ShowInfoDialogAsync(
                    "SavedLocationsFullTitle".GetLocalized(),
                    string.Format("SavedLocationsFullText".GetLocalized(), SavedLocationsService.MaxLocations));
                return;
            }

            await ReloadSavedLocationsAsync();

            // The saved list changed: update the tray data for all saved locations now.
            _ = TrayWeatherFile.RefreshAllSavedAsync(true);
        }

        private async void SavedLocationChip_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SavedLocation location })
                await SwitchToLocationAsync(location.PlaceId);

            // A ToggleButton flips its own state on click. Set it back to match the active location.
            UpdateActiveChip();
        }

        private void SavedLocationChip_OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (sender is not FrameworkElement { DataContext: SavedLocation location } element)
                return;

            args.Handled = true;

            var index = _renderedLocations.FindIndex(x => x.PlaceId == location.PlaceId);
            var iconFont = (Windows.UI.Xaml.Media.FontFamily) Application.Current.Resources["FluentIcons"];

            var moveLeft = new MenuFlyoutItem
            {
                Text = "SavedLocationMoveLeft".GetLocalized(),
                Icon = new FontIcon { FontFamily = iconFont, Glyph = "" },
                IsEnabled = index > 0
            };
            moveLeft.Click += async (_, _) => await MoveSavedLocationAsync(location.PlaceId, -1);

            var moveRight = new MenuFlyoutItem
            {
                Text = "SavedLocationMoveRight".GetLocalized(),
                Icon = new FontIcon { FontFamily = iconFont, Glyph = "" },
                IsEnabled = index >= 0 && index < _renderedLocations.Count - 1
            };
            moveRight.Click += async (_, _) => await MoveSavedLocationAsync(location.PlaceId, 1);

            var remove = new MenuFlyoutItem
            {
                Text = "SavedLocationRemove".GetLocalized(),
                Icon = new FontIcon { FontFamily = iconFont, Glyph = "" }
            };
            remove.Click += async (_, _) =>
            {
                await SavedLocationsService.RemoveAsync(location.PlaceId);
                await ReloadSavedLocationsAsync();
                _ = TrayWeatherFile.RefreshAllSavedAsync(true);
            };

            var flyout = new MenuFlyout();
            flyout.Items.Add(moveLeft);
            flyout.Items.Add(moveRight);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(remove);

            if (args.TryGetPosition(element, out var point))
                flyout.ShowAt(element, new FlyoutShowOptions { Position = point });
            else
                flyout.ShowAt(element);
        }

        private async Task MoveSavedLocationAsync(string placeId, int offset)
        {
            await SavedLocationsService.MoveAsync(placeId, offset);
            await ReloadSavedLocationsAsync();
        }

        private void InitializeSavedLocationShortcuts()
        {
            // Do not show accelerator hints in tooltips across the page.
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            // Ctrl+Tab and Ctrl+Shift+Tab cycle through saved locations.
            AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, () => CycleSavedLocation(1));
            AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => CycleSavedLocation(-1));

            // Ctrl+1 to Ctrl+9 jump to the first nine saved locations.
            for (var i = 0; i < 9; i++)
            {
                var index = i;
                AddAccelerator(VirtualKey.Number1 + i, VirtualKeyModifiers.Control, () => SwitchToSavedIndex(index));
                AddAccelerator(VirtualKey.NumberPad1 + i, VirtualKeyModifiers.Control, () => SwitchToSavedIndex(index));
            }
        }

        private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                action();
            };
            KeyboardAccelerators.Add(accelerator);
        }

        private void CycleSavedLocation(int step)
        {
            var count = _savedLocations.Count;
            if (count == 0)
                return;

            var current = -1;
            for (var i = 0; i < count; i++)
            {
                if (_savedLocations[i].PlaceId == _activePlaceId)
                {
                    current = i;
                    break;
                }
            }

            var next = current < 0
                ? (step > 0 ? 0 : count - 1)
                : ((current + step) % count + count) % count;

            SwitchToSavedIndex(next);
        }

        private void SwitchToSavedIndex(int index)
        {
            if (index < 0 || index >= _savedLocations.Count)
                return;

            _ = SwitchToLocationAsync(_savedLocations[index].PlaceId);
        }

        // Opens the window at a compact size once. After that, Windows remembers the size you choose.
        // The setting name changes when the default size changes, so the new size is applied once.
        private const double CompactWindowWidth = 820;
        private const double CompactWindowHeight = 600;
        private const string CompactWindowSettingKey = "compactWindowApplied2";

        private async Task ApplyCompactWindowSizeOnceAsync()
        {
            try
            {
                if (await ApplicationData.Current.LocalSettings.ReadAsync<bool>(CompactWindowSettingKey))
                    return;

                var view = ApplicationView.GetForCurrentView();
                // Fails while the window is maximized. It is then tried again at the next start.
                if (view.TryResizeView(new Windows.Foundation.Size(CompactWindowWidth, CompactWindowHeight)))
                    await ApplicationData.Current.LocalSettings.SaveAsync(CompactWindowSettingKey, true);
            }
            catch (Exception)
            {
                // Window size is cosmetic.
            }
        }

        // --- Settings height ---
        // The settings panel opens below the top bar. It fills the window height down to a small gap
        // at the bottom. The star rows between its sections share the extra height. In a short
        // window the panel keeps its natural height and scrolls.
        private const double SettingsTopOffset = 48;
        private const double SettingsBottomGap = 16;

        private void SettingsPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement panel)
                return;

            var available = Window.Current.Bounds.Height - SettingsTopOffset - SettingsBottomGap;
            panel.MinHeight = Math.Max(0, available);
        }

        // --- Tray icons ---
        // Setup registers the link type "fluentweather-tray:" for the tray program. Opening it starts
        // the tray icons. A running tray program ignores a second start. Windows may ask once whether
        // to switch apps.
        private static readonly Uri TrayStartUri = new("fluentweather-tray:start");

        private async void ShowTrayButton_OnClick(object sender, RoutedEventArgs e)
        {
            bool started;
            try
            {
                started = await Windows.System.Launcher.LaunchUriAsync(TrayStartUri);
            }
            catch (Exception)
            {
                started = false;
            }

            if (started || sender is not Button button)
                return;

            // The link type is missing: the tray icons are not installed on this PC.
            var label = button.Content;
            button.Content = "TrayNotInstalledText".GetLocalized();
            await Task.Delay(TimeSpan.FromSeconds(4));
            button.Content = label;
        }

        private async void ChangelogHyperlink_Click(Windows.UI.Xaml.Documents.Hyperlink sender,
            Windows.UI.Xaml.Documents.HyperlinkClickEventArgs args)
        {
            var dialog = new WhatsNewDialog();
            await dialog.ShowAsync();
        }

        // --- Top bar layout ---
        // The top bar holds the app name, the refresh button, the search box, the settings button
        // and the window buttons of Windows. The column widths follow the real width of the app name
        // and of the window buttons. In a narrow window the app name is hidden, so nothing overlaps.

        private const double TitleIconArea = 12 + 36 + 8;   // icon margin, icon with its P badge, gap column
        private const double TopBarButtonWidth = 52;
        private const double TopBarGap = 8;
        private const double MinSearchWidth = 150;
        private const double DefaultCaptionWidth = 138;   // three window buttons of 46 pixels

        private double _titleTextWidth;

        private void InitializeTitleBarLayout()
        {
            try
            {
                // Measure the app name once with a copy of its text block. A hidden text block measures 0.
                var probe = new TextBlock { Text = AppTitleTextBlock.Text, Style = AppTitleTextBlock.Style };
                probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                _titleTextWidth = Math.Ceiling(probe.DesiredSize.Width);
            }
            catch (Exception)
            {
                _titleTextWidth = 0;
            }
            if (_titleTextWidth <= 0)
                _titleTextWidth = 8 * (AppTitleTextBlock.Text?.Length ?? 0);

            CoreApplication.GetCurrentView().TitleBar.LayoutMetricsChanged += (_, _) => UpdateTitleBarLayout();
            SizeChanged += (_, _) => UpdateTitleBarLayout();
            UpdateTitleBarLayout();
        }

        private void UpdateTitleBarLayout()
        {
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            var captionWidth = (coreTitleBar.SystemOverlayRightInset > 0 ? coreTitleBar.SystemOverlayRightInset : DefaultCaptionWidth) + TopBarGap;
            var leftInset = coreTitleBar.SystemOverlayLeftInset;

            var needed = leftInset + TitleIconArea + _titleTextWidth + TopBarGap +
                         TopBarButtonWidth + TopBarGap + MinSearchWidth + TopBarGap +
                         TopBarButtonWidth + captionWidth;
            var showTitle = ActualWidth <= 0 || ActualWidth >= needed;
            AppTitleTextBlock.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;

            var titleArea = leftInset + TitleIconArea + (showTitle ? _titleTextWidth : 0) + TopBarGap;
            RefreshLeftColumn.Width = new GridLength(titleArea);
            LeftMarginColumn.Width = new GridLength(titleArea + TopBarButtonWidth + TopBarGap);
            SettingsRightColumn.Width = new GridLength(captionWidth);
            SearchRightColumn.Width = new GridLength(captionWidth + TopBarButtonWidth + TopBarGap);
        }

        // --- Language ---
        // Sets the language of the app text and of the weather text from the weather service.
        // Windows applies a new language when the app starts, so the app offers to restart.

        private List<string>? _languageTags;

        private List<string> GetLanguageTags()
        {
            if (_languageTags != null)
                return _languageTags;

            var tags = new List<string>();
            try
            {
                tags.AddRange(Windows.Globalization.ApplicationLanguages.ManifestLanguages
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(GetLanguageName, StringComparer.CurrentCultureIgnoreCase));
            }
            catch (Exception)
            {
                // Without the list only "Windows default" is offered.
            }

            tags.Insert(0, string.Empty);
            _languageTags = tags;
            return tags;
        }

        private static string GetLanguageName(string tag)
        {
            try
            {
                var language = new Windows.Globalization.Language(tag);
                return string.Equals(language.NativeName, language.DisplayName, StringComparison.CurrentCultureIgnoreCase)
                    ? language.NativeName
                    : language.NativeName + " (" + language.DisplayName + ")";
            }
            catch (Exception)
            {
                return tag;
            }
        }

        // Shows every language in a window of its own, where the list scrolls freely.
        private async void LanguageButton_OnClick(object sender, RoutedEventArgs e)
        {
            SettingsFlyout.Hide();

            var tags = GetLanguageTags();
            var list = new ListView
            {
                ItemsSource = LanguageChoices,
                SelectionMode = ListViewSelectionMode.Single,
                SelectedIndex = SettingsLanguageSelectedIndex,
                Height = 420,
                MinWidth = 320
            };

            ContentDialog picker = new()
            {
                Title = "LanguagePickerTitle".GetLocalized(),
                Content = list,
                PrimaryButtonText = "LanguagePickerUse".GetLocalized(),
                CloseButtonText = "LanguagePickerCancel".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };
            // A double-click on a language also chooses it.
            var doubleTapped = false;
            list.DoubleTapped += (_, _) =>
            {
                doubleTapped = true;
                picker.Hide();
            };

            // Show the chosen language in the visible part of the list.
            list.Loaded += (_, _) =>
            {
                if (list.SelectedItem != null)
                    list.ScrollIntoView(list.SelectedItem, ScrollIntoViewAlignment.Leading);
            };

            ContentDialogResult result;
            try
            {
                result = await picker.ShowAsync();
            }
            catch (Exception)
            {
                return;   // another dialog is open
            }

            if ((result != ContentDialogResult.Primary && !doubleTapped) || list.SelectedIndex < 0 || list.SelectedIndex >= tags.Count)
                return;

            await ApplyLanguageAsync(tags[list.SelectedIndex]);
        }

        private async Task ApplyLanguageAsync(string tag)
        {
            var current = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride ?? string.Empty;
            if (string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
                return;

            // An empty value returns to the Windows display language. Windows keeps this setting for the app.
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag;

            ContentDialog dialog = new()
            {
                Title = "LanguageRestartTitle".GetLocalized(),
                Content = "LanguageRestartText".GetLocalized(),
                PrimaryButtonText = "LanguageRestartNow".GetLocalized(),
                CloseButtonText = "LanguageRestartLater".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };

            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await CoreApplication.RequestRestartAsync(string.Empty);
            }
            catch (Exception)
            {
                // Another dialog is open, or a restart is not possible. The language applies at the next start.
            }
        }

        // --- Hourly forecast length ---
        // 0 shows the hours of the selected day (the original behavior). 24, 36 and 48 show that many hours.

        private static readonly int[] HourlyRangeSteps = [0, 24, 36, 48];
        private const string HourlyRangeSettingKey = "hourlyRangeHours";

        private int _hourlyRangeHours;
        private DateTimeOffset? _lastHourlyDay;

        private void InitializeHourlyRange()
        {
            try
            {
                //run in background to avoid a deadlock/ui freeze
                var saved = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>(HourlyRangeSettingKey, 0)).Result;
                _hourlyRangeHours = Array.IndexOf(HourlyRangeSteps, saved) >= 0 ? saved : 0;
            }
            catch (Exception)
            {
                _hourlyRangeHours = 0;
            }
        }

        private async void HourlyRangeSegmented_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var segmented = (Segmented) sender;

            if (!segmented.IsLoaded || segmented.SelectedIndex < 0 || segmented.SelectedIndex >= HourlyRangeSteps.Length)
                return;

            _hourlyRangeHours = HourlyRangeSteps[segmented.SelectedIndex];

            if (_lastHourlyDay != null)
                LoadHourlyData(_lastHourlyDay.Value);

            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(HourlyRangeSettingKey, _hourlyRangeHours);
            }
            catch (Exception)
            {
                // The choice still applies until the app closes.
            }
        }

        // --- Background contrast ---
        // 0 Original, 1 Dark, 2 Darker: light text. 3 Light, 4 Lighter: dark text.
        // A rectangle over the background image darkens or lightens it. The light choices also
        // switch the page to the light theme, so text, cards and title bar buttons turn dark.

        private const string BackgroundContrastSettingKey = "backgroundContrast";
        private const int DefaultBackgroundContrast = 1;
        private static readonly double[] BackgroundScrimOpacity = [0, 0.45, 0.65, 0.55, 0.72];
        private static readonly string[] AcrylicBrushKeys = ["MyAcrylicBrush", "MyAcrylicBrushLine", "MyAcrylicBrushTitleText"];

        private int _backgroundContrast = DefaultBackgroundContrast;
        private readonly Dictionary<string, (Color Tint, double TintOpacity, double? Luminosity, Color Fallback)> _originalBrushValues = new();

        private void InitializeBackgroundContrast()
        {
            try
            {
                //run in background to avoid a deadlock/ui freeze
                var saved = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>(BackgroundContrastSettingKey, DefaultBackgroundContrast)).Result;
                _backgroundContrast = saved is >= 0 and <= 4 ? saved : DefaultBackgroundContrast;
            }
            catch (Exception)
            {
                _backgroundContrast = DefaultBackgroundContrast;
            }

            try
            {
                //run in background to avoid a deadlock/ui freeze
                var savedTint = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>(CardTintSettingKey, DefaultCardTint)).Result;
                _cardTint = savedTint is >= 0 and <= 100 ? savedTint : DefaultCardTint;
            }
            catch (Exception)
            {
                _cardTint = DefaultCardTint;
            }

            foreach (var key in AcrylicBrushKeys)
            {
                if (Resources.TryGetValue(key, out var value) && value is Microsoft.UI.Xaml.Media.AcrylicBrush brush)
                    _originalBrushValues[key] = (brush.TintColor, brush.TintOpacity, brush.TintLuminosityOpacity, brush.FallbackColor);
            }

            ApplyBackgroundContrast(BackgroundImageToggleStatus);
        }

        private void ApplyBackgroundContrast(bool imagesOn)
        {
            var isLight = _backgroundContrast >= 3;

            BackgroundScrim.Fill = new Windows.UI.Xaml.Media.SolidColorBrush(isLight ? Colors.White : Colors.Black);
            BackgroundScrim.Opacity = imagesOn ? BackgroundScrimOpacity[_backgroundContrast] : 0;

            RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Default;

            // Restore the original brushes, then change them for dark text.
            foreach (var pair in _originalBrushValues)
            {
                if (Resources[pair.Key] is Microsoft.UI.Xaml.Media.AcrylicBrush brush)
                {
                    brush.TintColor = pair.Value.Tint;
                    brush.TintOpacity = pair.Value.TintOpacity;
                    brush.TintLuminosityOpacity = pair.Value.Luminosity;
                    brush.FallbackColor = pair.Value.Fallback;
                }
            }

            if (isLight)
            {
                SetAcrylic("MyAcrylicBrush", Colors.White, 0.3, 0.3, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                SetAcrylic("MyAcrylicBrushLine", Colors.Black, 0, 0.1, Color.FromArgb(0x1D, 0x00, 0x00, 0x00));
                SetAcrylic("MyAcrylicBrushTitleText", Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B), 0.7, 0.8, Color.FromArgb(0xDD, 0x1B, 0x1B, 0x1B));
            }

            ApplyCardTint(isLight);

            // The radar map has a light and a dark version.
            if (RadarSection != null && RadarSection.Visibility == Visibility.Visible)
                _ = BuildRadarAsync();

            // The title bar buttons are drawn by Windows. Give them the same text color as the page.
            ThemeSelectorService.TitleBarTheme = isLight ? ElementTheme.Light : null;
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
                titleBar.ButtonInactiveForegroundColor = isLight ? Color.FromArgb(0xFF, 0x60, 0x60, 0x60) : Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0);
                titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
                titleBar.ButtonHoverBackgroundColor = isLight ? Color.FromArgb(0x19, 0x00, 0x00, 0x00) : Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF);
            }
            catch (Exception)
            {
                // Title bar colors are cosmetic.
            }
        }

        private void SetAcrylic(string key, Color tint, double tintOpacity, double luminosity, Color fallback)
        {
            if (Resources[key] is not Microsoft.UI.Xaml.Media.AcrylicBrush brush)
                return;

            brush.TintColor = tint;
            brush.TintOpacity = tintOpacity;
            brush.TintLuminosityOpacity = luminosity;
            brush.FallbackColor = fallback;
        }

        private async void BackgroundContrastSegmented_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var segmented = (Segmented) sender;

            if (!segmented.IsLoaded || segmented.SelectedIndex < 0 || segmented.SelectedIndex > 4)
                return;

            _backgroundContrast = segmented.SelectedIndex;
            ApplyBackgroundContrast(BackgroundImageToggleStatus);

            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(BackgroundContrastSettingKey, _backgroundContrast);
            }
            catch (Exception)
            {
                // The choice still applies until the app closes.
            }
        }

        // --- Card tint ---
        // 0 to 100 percent. 0 keeps the original look. Above 0, the cards get a tinted acrylic
        // background: dark with light text, white with dark text (the light background contrasts).
        // The cards are the detail chips, the day buttons and the panels that use MyAcrylicBrush.
        // The selected day keeps its own dark fill.

        private const string CardTintSettingKey = "cardTint";
        private const int DefaultCardTint = 40;
        private static readonly Color CardTintDark = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E);

        private int _cardTint = DefaultCardTint;

        // The percentage next to the slider. It is inside the settings flyout, so the code gets it when it loads.
        private TextBlock? _cardTintValueText;

        private void CardTintValueText_OnLoaded(object sender, RoutedEventArgs e)
        {
            _cardTintValueText = sender as TextBlock;
            if (_cardTintValueText != null)
                _cardTintValueText.Text = _cardTint + "%";
        }

        private void ApplyCardTint(bool isLight)
        {
            var t = _cardTint / 100.0;

            if (_cardTint > 0)
            {
                if (isLight)
                {
                    var lt = Math.Max(0.3, t);
                    SetAcrylic("MyAcrylicBrush", Colors.White, lt, lt, Color.FromArgb((byte) Math.Round(lt * 255), 0xFF, 0xFF, 0xFF));
                }
                else
                {
                    SetAcrylic("MyAcrylicBrush", CardTintDark, t, t, Color.FromArgb((byte) Math.Round(t * 255), CardTintDark.R, CardTintDark.G, CardTintDark.B));
                }
            }

            var chipBrush = _cardTint > 0 ? Resources["MyAcrylicBrush"] as Windows.UI.Xaml.Media.Brush : null;
            foreach (var chip in new[] { WindChipControl, PressureChipControl, HumidityChipControl, VisibilityChipControl, DewPointChipControl, UVIndexChipControl, AirQualityChipControl })
                chip?.SetCardBackground(chipBrush);

            foreach (var pastCard in PastDaysPanel.Children.OfType<Button>())
                ApplyCardTintToDayButton(pastCard);

            var count = RepeaterDays.ItemsSourceView?.Count ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (RepeaterDays.TryGetElement(i) is Button dayButton)
                    ApplyCardTintToDayButton(dayButton);
            }

            if (_cardTintValueText != null)
                _cardTintValueText.Text = _cardTint + "%";
        }

        private void ApplyCardTintToDayButton(Button button)
        {
            if (_cardTint > 0 && button != _lastSelectedDayButton && Resources["MyAcrylicBrush"] is Windows.UI.Xaml.Media.Brush brush)
                button.Background = brush;
            else
                button.ClearValue(Control.BackgroundProperty);
        }

        private async void CardTintSlider_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var value = (int) Math.Round(e.NewValue / 5.0) * 5;
            if (_cardTintValueText != null)
                _cardTintValueText.Text = value + "%";

            if (sender is not Slider slider || !slider.IsLoaded || value == _cardTint)
                return;

            _cardTint = Math.Clamp(value, 0, 100);
            ApplyBackgroundContrast(BackgroundImageToggleStatus);

            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(CardTintSettingKey, _cardTint);
            }
            catch (Exception)
            {
                // The choice still applies until the app closes.
            }
        }

        // --- Air quality ---
        // One small request to Open-Meteo per location and hour. See AirQualityService.
        // US locations use the US EPA scale, other locations the European scale.

        private async Task UpdateAirQualityAsync(double latitude, double longitude, string? countryCode, string placeId)
        {
            try
            {
                var data = await AirQualityService.GetAsync(latitude, longitude);

                // Another location may be shown by now.
                if (_displayedPlaceId != placeId)
                    return;

                var usScale = countryCode?.ToUpperInvariant() is "US" or "PR" or "VI" or "GU" or "MP" or "AS";
                var index = usScale ? data?.UsAqi : data?.EuropeanAqi;
                if (data == null || index == null)
                {
                    AirQualityChipControl.Visibility = Visibility.Collapsed;
                    return;
                }

                var value = (int) Math.Round(index.Value);
                AirQualityChipControl.Value = value + " " + AirQualityService.CategoryText(value, usScale);

                // The readings behind the index, and the credit that the data license asks for.
                var unit = " µg/m³";
                string Reading(string name, double? amount) => amount == null ? "" : "\n" + name + ": " + Math.Round(amount.Value) + unit;
                var tip = (usScale ? "AirQualityUsScale" : "AirQualityEuScale").GetLocalized() + ": " + value
                          + Reading("PM2.5", data.Pm25) + Reading("PM10", data.Pm10)
                          + Reading("AirQualityOzone".GetLocalized(), data.Ozone)
                          + Reading("AirQualityNitrogenDioxide".GetLocalized(), data.NitrogenDioxide)
                          + "\n\n" + "AirQualityCredit".GetLocalized();
                ToolTipService.SetToolTip(AirQualityChipControl, tip);
                AirQualityChipControl.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                AirQualityChipControl.Visibility = Visibility.Collapsed;
            }
        }

        // --- Past days ---
        // The recorded days before today, left of today in the day row. See PastDaysService.
        // The cards show what was recorded. They have no hourly view, so a click does nothing.

        private async Task UpdatePastDaysAsync(RootV3Response data, string placeId)
        {
            try
            {
                var observation = data.v3wxobservationscurrent;
                var units = (await VariousUtils.GetUnitsCode()).ToString();

                if (await SavedLocationsService.ContainsAsync(placeId))
                {
                    await PastDaysService.UpdateScheduleAsync(placeId, data.v3wxforecasthourly10day, observation.validTimeLocal.Offset);
                    var part = data.v3wxforecastdaily10day?.daypart?.FirstOrDefault();
                    await PastDaysService.ObserveAsync(placeId, observation, part?.iconCode, part?.wxPhraseLong, part?.precipChance, units);
                }

                await ShowPastDaysAsync(placeId, observation.validTimeLocal, units);
                _ = PastDaysService.RunDueCapturesAsync();
            }
            catch (Exception)
            {
                // The day row still shows today and the forecast.
            }
        }

        private async Task ShowPastDaysAsync(string placeId, DateTimeOffset locationNow, string units)
        {
            var records = await PastDaysService.GetPastDaysAsync(placeId, locationNow, units);

            // Another location may be shown by now.
            if (_displayedPlaceId != placeId)
                return;

            PastDaysPanel.Children.Clear();
            foreach (var record in records)
            {
                var card = CreatePastDayCard(record, locationNow);
                ApplyCardTintToDayButton(card);
                PastDaysPanel.Children.Add(card);
            }

            PastDaysPanel.Visibility = records.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Today stays the first day in view. The past days are reached by scrolling left.
            DaysScroll.UpdateLayout();
            DaysScroll.ChangeView(records.Count > 0 ? PastDaysPanel.ActualWidth + PastDaysPanel.Margin.Right : 0, null, null, true);
        }

        private static Button CreatePastDayCard(PastDayRecord record, DateTimeOffset locationNow)
        {
            var date = DateTime.ParseExact(record.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string title;
            if (date.Date == locationNow.Date.AddDays(-1))
            {
                title = "PastDaysYesterday".GetLocalized();
            }
            else
            {
                var language = Windows.System.UserProfile.GlobalizationPreferences.Languages[0];
                var dayName = new System.Globalization.CultureInfo(language).DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek);
                title = VariousUtils.UppercaseFirst(dayName) + " " + date.Day;
            }

            var panel = new RelativePanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            if (record.IconCode != null)
            {
                var icon = new Image
                {
                    Width = 28,
                    Source = new Media.Imaging.SvgImageSource { UriSource = new Uri("ms-appx:///Assets/weticons/" + record.IconCode + ".svg") }
                };
                RelativePanel.SetAlignTopWithPanel(icon, true);
                RelativePanel.SetAlignLeftWithPanel(icon, true);
                panel.Children.Add(icon);
            }

            var titleText = new TextBlock { Text = title };
            RelativePanel.SetAlignTopWithPanel(titleText, true);
            RelativePanel.SetAlignRightWithPanel(titleText, true);
            panel.Children.Add(titleText);

            var temperatures = (record.High?.ToString() ?? "--") + "° / " + (record.Low?.ToString() ?? "--") + "°";
            var temperatureText = new TextBlock { Text = temperatures };
            RelativePanel.SetAlignBottomWithPanel(temperatureText, true);
            RelativePanel.SetAlignLeftWithPanel(temperatureText, true);
            panel.Children.Add(temperatureText);

            if (record.PrecipChance != null)
            {
                var chanceText = new TextBlock { Text = record.PrecipChance + "%" };
                RelativePanel.SetAlignBottomWithPanel(chanceText, true);
                RelativePanel.SetAlignRightWithPanel(chanceText, true);
                panel.Children.Add(chanceText);

                var drop = new Image
                {
                    Width = 16,
                    Stretch = Media.Stretch.Uniform,
                    Margin = new Thickness(0, 0, 4, 0),
                    Source = new Media.Imaging.SvgImageSource
                    {
                        UriSource = new Uri("ms-appx:///Assets/varicons/" + VariousUtils.GetPrecipIconChance(record.PrecipChance) + ".svg")
                    }
                };
                RelativePanel.SetAlignBottomWithPanel(drop, true);
                RelativePanel.SetLeftOf(drop, chanceText);
                panel.Children.Add(drop);
            }

            var card = new Button
            {
                Width = 135,
                Height = 70,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = panel
            };

            var description = string.IsNullOrEmpty(record.Phrase) ? title : title + ": " + record.Phrase;
            ToolTipService.SetToolTip(card, description);
            AutomationProperties.SetName(card, description + ", " + temperatures);
            return card;
        }

        // --- Day row scrolling ---
        // Every day card is 135 pixels wide with 8 pixels between cards, the past day cards too.
        // So each card starts at a multiple of 143 pixels. The row can be scrolled freely with the
        // scroll bar, touch or a touchpad. When the scroll ends it moves to the nearest card, so no
        // card is left cut off. Each mouse wheel notch over the row moves it by exactly one card,
        // whatever the Windows wheel setting is. The arrow keys move one card, Home and End go to
        // the ends. When all days fit, the row does not scroll and the wheel scrolls the page.

        private const double DayCardPitch = 143;
        private const int WheelNotch = 120;

        private int _dayWheelDelta;

        private double SnapDayOffset(double offset)
        {
            var max = DaysScroll.ScrollableWidth;
            if (max <= 0)
                return 0;

            // The end of the row is a stop too, so the last card can be seen in full.
            if (max - offset < DayCardPitch / 2)
                return max;

            return Math.Clamp(Math.Round(offset / DayCardPitch) * DayCardPitch, 0, max);
        }

        private DispatcherTimer? _daySnapTimer;

        // The row snaps a quarter second after the scrolling stops, so dragging the scroll bar
        // stays smooth.
        private void DaysScroll_OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_daySnapTimer == null)
            {
                _daySnapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _daySnapTimer.Tick += (_, _) =>
                {
                    _daySnapTimer.Stop();
                    var target = SnapDayOffset(DaysScroll.HorizontalOffset);
                    if (Math.Abs(target - DaysScroll.HorizontalOffset) > 0.5)
                        DaysScroll.ChangeView(target, null, null, false);
                };
            }

            _daySnapTimer.Stop();
            _daySnapTimer.Start();
        }

        private void StepDays(int cards)
        {
            var from = SnapDayOffset(DaysScroll.HorizontalOffset);
            var target = Math.Clamp(from + cards * DayCardPitch, 0, DaysScroll.ScrollableWidth);
            DaysScroll.ChangeView(SnapDayOffset(target), null, null, false);
        }

        private void DaysRowPanel_OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (DaysScroll.ScrollableWidth <= 0)
                return; // all days fit, so the wheel scrolls the page

            // A horizontal wheel turns positive to the right. A vertical wheel turns positive upward,
            // which moves the row back toward the past days.
            var properties = e.GetCurrentPoint(DaysScroll).Properties;
            var delta = properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : -properties.MouseWheelDelta;
            e.Handled = true;

            // Small wheel movements, from some touchpads, add up to one notch.
            if (Math.Sign(delta) != Math.Sign(_dayWheelDelta))
                _dayWheelDelta = 0;
            _dayWheelDelta += delta;

            var cards = _dayWheelDelta / WheelNotch;
            if (cards == 0)
                return;

            _dayWheelDelta -= cards * WheelNotch;
            StepDays(cards);
        }

        private void DaysRowPanel_OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Left:
                    StepDays(-1);
                    break;
                case VirtualKey.Right:
                    StepDays(1);
                    break;
                case VirtualKey.Home:
                    DaysScroll.ChangeView(0, null, null, false);
                    break;
                case VirtualKey.End:
                    DaysScroll.ChangeView(DaysScroll.ScrollableWidth, null, null, false);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        // --- Radar (US locations) ---
        // A map of the area around the location, drawn from three free public sources:
        //   USGS National Map shaded relief tiles (terrain),
        //   NOAA/NWS MRMS base reflectivity (radar), and NOAA/NWS geopolitical borders.
        // The section is shown only for locations inside a NOAA radar area. The map is built again
        // when the data refreshes, when the zoom or the panel width changes, and when the
        // background contrast switches between the light and the dark map.

        private const string RadarZoomSettingKey = "radarZoom";
        private const int DefaultRadarZoom = 7;
        private const int MinRadarZoom = 4;
        private const int MaxRadarZoom = 10;
        private const double WebMercatorHalf = 20037508.342789244;

        private static readonly HttpClient RadarHttp = ApiUtils.CreateClient("https://opengeo.ncep.noaa.gov/");

        private int _radarZoom = DefaultRadarZoom;
        private double _radarLat;
        private double _radarLon;
        private string? _radarRegion;
        private double _radarBuiltWidth;
        private int _radarBuildId;
        private DispatcherTimer? _radarResizeTimer;

        private void InitializeRadar()
        {
            try
            {
                //run in background to avoid a deadlock/ui freeze
                var saved = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>(RadarZoomSettingKey, DefaultRadarZoom)).Result;
                _radarZoom = saved is >= MinRadarZoom and <= MaxRadarZoom ? saved : DefaultRadarZoom;
            }
            catch (Exception)
            {
                _radarZoom = DefaultRadarZoom;
            }

            _radarResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _radarResizeTimer.Tick += (_, _) =>
            {
                _radarResizeTimer?.Stop();
                _ = BuildRadarAsync();
            };
            UpdateRadarZoomButtons();
        }

        // The NOAA radar workspace for a location, or null when there is no NOAA radar there.
        private static string? GetRadarRegion(double lat, double lon, string? countryCode)
        {
            var cc = countryCode?.ToUpperInvariant();
            if (cc is not ("US" or "PR" or "VI" or "GU" or "MP"))
                return null;

            if (lat is >= 18 and <= 23 && lon is >= -161 and <= -154) return "hawaii";
            if (lat >= 51 && (lon <= -129 || lon >= 170)) return "alaska";
            if (lat is >= 17 and <= 19.5 && lon is >= -68 and <= -64) return "carib";
            if (lat is >= 13 and <= 21 && lon is >= 144 and <= 147) return "guam";
            if (lat is >= 20 and <= 50 && lon is >= -127 and <= -65) return "conus";
            return null;
        }

        private void UpdateRadar(double lat, double lon, string? countryCode)
        {
            _radarLat = lat;
            _radarLon = lon;
            _radarRegion = GetRadarRegion(lat, lon, countryCode);
            RadarSection.Visibility = _radarRegion == null ? Visibility.Collapsed : Visibility.Visible;

            if (_radarRegion != null)
                _ = BuildRadarAsync();
        }

        private async Task BuildRadarAsync()
        {
            var id = ++_radarBuildId;
            var w = Math.Floor(RadarMapHost.ActualWidth);
            var h = Math.Floor(RadarMapHost.ActualHeight);
            if (_radarRegion == null || w < 50 || h < 50)
                return;

            _radarBuiltWidth = w;
            var region = _radarRegion;
            var z = _radarZoom;
            var isLight = _backgroundContrast >= 3;

            // Web Mercator pixel position of the location at this zoom, then the top left corner of the map.
            var n = 256.0 * Math.Pow(2, z);
            var cx = (_radarLon + 180) / 360 * n;
            var sin = Math.Sin(_radarLat * Math.PI / 180);
            var cy = (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * n;
            var x0 = cx - w / 2;
            var y0 = cy - h / 2;

            RadarCanvas.Children.Clear();
            RadarCanvas.Width = w;
            RadarCanvas.Height = h;
            RadarCanvas.Clip = new Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) };
            RadarCanvas.Children.Add(new Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = new Media.SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xEE, 0xEE, 0xF0) : Color.FromArgb(0xFF, 0x18, 0x18, 0x1C))
            });

            // Terrain tiles
            var tileCount = 1 << z;
            for (var ty = (int) Math.Floor(y0 / 256); ty <= (int) Math.Floor((y0 + h - 1) / 256); ty++)
            {
                if (ty < 0 || ty >= tileCount)
                    continue;

                for (var tx = (int) Math.Floor(x0 / 256); tx <= (int) Math.Floor((x0 + w - 1) / 256); tx++)
                {
                    var wrappedX = ((tx % tileCount) + tileCount) % tileCount;
                    var tile = new Image
                    {
                        Width = 256,
                        Height = 256,
                        Stretch = Media.Stretch.Fill,
                        Opacity = isLight ? 0.9 : 0.28,
                        Source = new BitmapImage(new Uri("https://basemap.nationalmap.gov/arcgis/rest/services/USGSShadedReliefOnly/MapServer/tile/"
                                                         + z + "/" + ty + "/" + wrappedX))
                    };
                    Canvas.SetLeft(tile, tx * 256 - x0);
                    Canvas.SetTop(tile, ty * 256 - y0);
                    RadarCanvas.Children.Add(tile);
                }
            }

            // Radar and borders: one picture each for the whole map, at twice the size for sharp lines.
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var bbox = string.Join(",", new[]
            {
                x0 / n * 2 * WebMercatorHalf - WebMercatorHalf,
                WebMercatorHalf - (y0 + h) / n * 2 * WebMercatorHalf,
                (x0 + w) / n * 2 * WebMercatorHalf - WebMercatorHalf,
                WebMercatorHalf - y0 / n * 2 * WebMercatorHalf
            }.Select(v => v.ToString("0.###", ci)));

            var size = "&WIDTH=" + (int) (w * 2) + "&HEIGHT=" + (int) (h * 2);
            var common = "SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&STYLES=&CRS=EPSG:3857&BBOX=" + bbox + size + "&FORMAT=image/png&TRANSPARENT=true";
            var layer = region + "_bref_qcd";

            // A new address each minute, so the image cache never returns an old radar picture.
            var stamp = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            var radarUrl = "https://opengeo.ncep.noaa.gov/geoserver/" + region + "/" + layer + "/ows?" + common + "&LAYERS=" + layer + "&t=" + stamp;
            var radarImage = new Image { Width = w, Height = h, Stretch = Media.Stretch.Fill, Opacity = 0.85 };
            RadarCanvas.Children.Add(radarImage);

            var borders = new Image { Width = w, Height = h, Stretch = Media.Stretch.Fill };
            RadarCanvas.Children.Add(borders);

            // The location
            Media.Brush dotBrush;
            try
            {
                dotBrush = new Media.SolidColorBrush((Color) Application.Current.Resources["SystemAccentColorLight2"]);
            }
            catch (Exception)
            {
                dotBrush = new Media.SolidColorBrush(Colors.DodgerBlue);
            }

            var dot = new Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = dotBrush,
                Stroke = new Media.SolidColorBrush(isLight ? Colors.Black : Colors.White),
                StrokeThickness = 2
            };
            Canvas.SetLeft(dot, w / 2 - 7);
            Canvas.SetTop(dot, h / 2 - 7);
            RadarCanvas.Children.Add(dot);

            RadarTimeText.Text = "RadarLoadingText".GetLocalized();

            // The radar. The app checks the picture, so it can say when there is no rain or snow on the map.
            string status;
            try
            {
                using var radarResponse = await ApiUtils.GetWithRetryAsync(RadarHttp, radarUrl);
                radarResponse.EnsureSuccessStatusCode();
                var radarPng = await radarResponse.Content.ReadAsByteArrayAsync();
                var (radarSource, hasEchoes) = await DecodePngAsync(radarPng, false);
                if (id != _radarBuildId)
                    return;

                radarImage.Source = radarSource;
                string time;
                try
                {
                    time = await VariousUtils.GetTimeBasedOnUserSettings(DateTime.Now);
                }
                catch (Exception)
                {
                    time = DateTime.Now.ToString("t");
                }

                status = "RadarUpdatedText".GetLocalized() + " " + time;
                if (!hasEchoes)
                    status += ". " + "RadarNoEchoesText".GetLocalized();
            }
            catch (Exception)
            {
                status = "RadarFailedText".GetLocalized();
            }

            if (id == _radarBuildId)
                RadarTimeText.Text = status;

            // NOAA draws the borders black. The dark map gets them in a light gray.
            try
            {
                var bordersUrl = "https://opengeo.ncep.noaa.gov/geoserver/ows?" + common + "&LAYERS=geopolitical";
                using var response = await ApiUtils.GetWithRetryAsync(RadarHttp, bordersUrl);
                response.EnsureSuccessStatusCode();
                var png = await response.Content.ReadAsByteArrayAsync();
                var (source, _) = await DecodePngAsync(png, !isLight);
                if (id == _radarBuildId)
                    borders.Source = source;
            }
            catch (Exception)
            {
                // The map still shows the terrain and the radar.
            }
        }

        // Decodes a PNG for an Image. With recolorLight, every visible pixel turns light gray (for the
        // borders on the dark map). Also returns whether the picture has any visible pixel.
        private static async Task<(Media.ImageSource Source, bool HasPixels)> DecodePngAsync(byte[] png, bool recolorLight)
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(png));
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var pixels = pixelData.DetachPixelData();

            // Premultiplied BGRA: each color value is the color times the alpha.
            var hasPixels = false;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a == 0)
                    continue;

                hasPixels = true;
                if (!recolorLight)
                    break;

                pixels[i] = (byte) (0xD2 * a / 255);
                pixels[i + 1] = (byte) (0xC8 * a / 255);
                pixels[i + 2] = (byte) (0xC8 * a / 255);
            }

            var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pixels),
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                (int) decoder.PixelWidth,
                (int) decoder.PixelHeight,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return (source, hasPixels);
        }

        private void RadarMapHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_radarRegion == null || _radarResizeTimer == null)
                return;

            if (Math.Abs(Math.Floor(e.NewSize.Width) - _radarBuiltWidth) < 24)
                return;

            _radarResizeTimer.Stop();
            _radarResizeTimer.Start();
        }

        private async void ChangeRadarZoom(int delta)
        {
            var zoom = Math.Clamp(_radarZoom + delta, MinRadarZoom, MaxRadarZoom);
            if (zoom == _radarZoom)
                return;

            _radarZoom = zoom;
            UpdateRadarZoomButtons();
            _ = BuildRadarAsync();

            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(RadarZoomSettingKey, _radarZoom);
            }
            catch (Exception)
            {
                // The zoom still applies until the app closes.
            }
        }

        private void UpdateRadarZoomButtons()
        {
            RadarZoomInButton.IsEnabled = _radarZoom < MaxRadarZoom;
            RadarZoomOutButton.IsEnabled = _radarZoom > MinRadarZoom;
        }

        private void RadarZoomInButton_OnClick(object sender, RoutedEventArgs e) => ChangeRadarZoom(1);

        private void RadarZoomOutButton_OnClick(object sender, RoutedEventArgs e) => ChangeRadarZoom(-1);

        // --- Text and picture size ---
        // The main area is laid out at (window size / scale) and then drawn smaller with a ScaleTransform.
        // This makes all text, icons and pictures smaller in the same proportion, and more of the
        // forecast fits in a small window. The title bar row with the search box is not scaled.

        private static readonly int[] UiScaleSteps = [50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100];
        private const int DefaultUiScalePercent = 100;
        private const string UiScaleSettingKey = "uiScalePercent";

        private int _uiScalePercent = DefaultUiScalePercent;

        private void InitializeUiScale()
        {
            try
            {
                //run in background to avoid a deadlock/ui freeze
                var saved = Task.Run(async () => await ApplicationData.Current.LocalSettings.ReadAsync<int>(UiScaleSettingKey, DefaultUiScalePercent)).Result;
                _uiScalePercent = NormalizeUiScale(saved);
            }
            catch (Exception)
            {
                _uiScalePercent = DefaultUiScalePercent;
            }

            // Ctrl+Minus and Ctrl+Plus change the size. Ctrl+0 returns to the default size.
            AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => StepUiScale(-1));
            AddAccelerator((VirtualKey)189, VirtualKeyModifiers.Control, () => StepUiScale(-1));   // minus key
            AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => StepUiScale(1));
            AddAccelerator((VirtualKey)187, VirtualKeyModifiers.Control, () => StepUiScale(1));    // equals/plus key
            AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => _ = SetUiScaleAsync(DefaultUiScalePercent));
            AddAccelerator(VirtualKey.NumberPad0, VirtualKeyModifiers.Control, () => _ = SetUiScaleAsync(DefaultUiScalePercent));
        }

        private static int NormalizeUiScale(int percent)
        {
            return Array.IndexOf(UiScaleSteps, percent) >= 0 ? percent : DefaultUiScalePercent;
        }

        private void ZoomHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyUiScale();
        }

        private void ApplyUiScale()
        {
            var width = ZoomHost.ActualWidth;
            var height = ZoomHost.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var scale = _uiScalePercent / 100.0;
            MainInnerContentGrid.Width = width / scale;
            MainInnerContentGrid.Height = height / scale;
            MainInnerContentGrid.RenderTransform = new Windows.UI.Xaml.Media.ScaleTransform { ScaleX = scale, ScaleY = scale };
            ZoomHost.Clip = new Windows.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
        }

        private async Task SetUiScaleAsync(int percent)
        {
            percent = NormalizeUiScale(percent);
            if (percent == _uiScalePercent)
                return;

            _uiScalePercent = percent;
            ApplyUiScale();

            try
            {
                await ApplicationData.Current.LocalSettings.SaveAsync(UiScaleSettingKey, percent);
            }
            catch (Exception)
            {
                // The size still applies until the app closes.
            }
        }

        private void StepUiScale(int step)
        {
            var index = Array.IndexOf(UiScaleSteps, _uiScalePercent);
            if (index < 0)
                index = Array.IndexOf(UiScaleSteps, DefaultUiScalePercent);

            var next = Math.Clamp(index + step, 0, UiScaleSteps.Length - 1);
            _ = SetUiScaleAsync(UiScaleSteps[next]);
        }

        private void UiScaleSmallerButton_OnClick(object sender, RoutedEventArgs e)
        {
            StepUiScale(-1);
            ShowUiScaleValue(sender);
        }

        private void UiScaleLargerButton_OnClick(object sender, RoutedEventArgs e)
        {
            StepUiScale(1);
            ShowUiScaleValue(sender);
        }

        private void UiScaleResetButton_OnClick(object sender, RoutedEventArgs e)
        {
            _ = SetUiScaleAsync(DefaultUiScalePercent);
            ShowUiScaleValue(sender);
        }

        // Updates the percentage between the buttons. It is in the flyout template, so it is found by its Tag.
        private void ShowUiScaleValue(object sender)
        {
            if ((sender as FrameworkElement)?.Parent is not Panel panel)
                return;

            foreach (var child in panel.Children)
            {
                if (child is TextBlock { Tag: "UiScaleValue" } label)
                    label.Text = UiScaleText;
            }
        }

        private static async Task ShowInfoDialogAsync(string title, string content)
        {
            ContentDialog dialog = new()
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // Another dialog is already open. The message is not critical.
            }
        }
    }
}
