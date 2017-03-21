﻿using LineDietXF.Converters;
using LineDietXF.Extensions;
using LineDietXF.Helpers;
using LineDietXF.Interfaces;
using LineDietXF.Types;
using LineDietXF.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism;
using Prism.Commands;
using Prism.Navigation;
using Prism.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Xamarin.Forms;

namespace LineDietXF.ViewModels
{
    /// <summary>
    /// The second tab of the app which shows the OxyPlot graph and a listing of recent weight entries
    /// </summary>
    public class GraphPageViewModel : BaseViewModel, IActiveAware
    {
        static OxyColor GRID_LINES_COLOR_MINOR = Constants.UI.GRAPH_MinorGridLines.ToOxyColor();
        static OxyColor GRID_LINES_COLOR_MAJOR = Constants.UI.GRAPH_MajorGridLines.ToOxyColor();
        static OxyColor GRID_LINES_COLOR_BORDER = Constants.UI.GRAPH_BorderColor.ToOxyColor();

        // The graph has several breakpoints for how the date axis is labeled and how often those labels appear
        // This roughly breaks down to a a day scale, week scale, thirty day scale, and year scale
        const int DayScale_MinorStep = 1;
        const int DayScale_MajorStep = 1;
        const string DayScale_AxisFormat = "M/d";

        const int WeekScale_BreakPoint = 7;
        const int WeekScale_MinorStep = 1;
        const int WeekScale_MajorStep = 7;
        const string WeekScale_AxisFormat = "M/d";

        const int ThirtyDayScale_BreakPoint = 90;
        const int ThirtyDayScale_MinorStep = 7;
        const int ThirtyDayScale_MajorStep = 30;
        const string ThirtyDayScale_AxisFormat = "MMM d";

        const int YearScale_BreakPoint = 280;
        const int YearScale_MinorStep = 30;
        const int YearScale_MajorStep = 90;
        const string YearScale_AxisFormat = "MMM";

        // Bindable Properties
        private ObservableCollection<WeightEntry> _latestWeightEntries;
        public ObservableCollection<WeightEntry> LatestWeightEntries
        {
            get { return _latestWeightEntries; }
            set { SetProperty(ref _latestWeightEntries, value); }
        }

        PlotModel _plotModel;
        public PlotModel PlotModel
        {
            get { return _plotModel; }
            set { SetProperty(ref _plotModel, value); }
        }

        bool _isWeightListingVisible;
        public bool IsWeightListingVisible
        {
            get { return _isWeightListingVisible; }
            set { SetProperty(ref _isWeightListingVisible, value); }
        }

        public WeightEntry SelectedWeight
        {
            get { return null; }
            set
            {
                if (value != null)
                    EditEntry(value);
            }
        }

        string _placeholderText = Constants.Strings.GraphPage_PlaceholderText_Loading;
        public string PlaceholderText
        {
            get { return _placeholderText; }
            set { SetProperty(ref _placeholderText, value); }
        }

        #region IActiveAware implementation

        public event EventHandler IsActiveChanged;

        private bool _isActive;
        public bool IsActive
        {
            get { return _isActive; }
            set
            {
                _isActive = value;
                if (IsActiveChanged != null)
                    IsActiveChanged(this, EventArgs.Empty);

                if (_isActive)
                    Setup();
                else
                    TearDown();
            }
        }

        #endregion

        // Services
        IDataService DataService { get; set; }
        IWindowColorService WindowColorService { get; set; }

        // Bindable Commands
        public DelegateCommand AddEntryCommand { get; set; }
        public DelegateCommand<WeightEntry> DeleteEntryCommand { get; set; }

        public GraphPageViewModel(INavigationService navigationService, IAnalyticsService analyticsService, IPageDialogService dialogService,
            IDataService dataService, IWindowColorService windowColorService) :
            base(navigationService, analyticsService, dialogService)
        {
            DataService = dataService;
            WindowColorService = windowColorService;

            AddEntryCommand = new DelegateCommand(ShowAddWeightScreen);
            DeleteEntryCommand = new DelegateCommand<WeightEntry>(ConfirmDeleteItem);
        }

        void Setup()
        {
            AnalyticsService.TrackPageView(Constants.Analytics.Page_Graph);

            // wire up events
            DataService.UserDataUpdated += DataService_UserDataUpdated;

            // The first run of the app the data service may not have finished loading yet. In that scenario it will fire UserDataUpdated() once done init'ing which will cause the RefreshFromuserDataAsync() to be called.
            // On subsequent navigations it will have already been init'd and should refresh data when returning to this page
            if (DataService.HasBeenInitialized)
                RefreshFromUserDataAsync();
        }

        void TearDown()
        {
            // unwire from events
            if (DataService != null)
                DataService.UserDataUpdated -= DataService_UserDataUpdated;

            UnwireDateAxisChangedEvent();
        }

        void UnwireDateAxisChangedEvent()
        {
            if (PlotModel != null && PlotModel.Axes != null && PlotModel.Axes.Count > 0)
            {
                PlotModel.Axes[0].AxisChanged -= DateAxis_Changed;
            }
        }

        void DataService_UserDataUpdated(object sender, EventArgs e)
        {
            RefreshFromUserDataAsync();
        }

        async void ShowAddWeightScreen()
        {
            AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_LaunchedAddWeight, 1);

            await NavigationService.NavigateAsync($"{nameof(NavigationPage)}/{nameof(WeightEntryPage)}", useModalNavigation: true);
        }

        async void RefreshFromUserDataAsync()
        {
            try
            {
                IncrementPendingRequestCount(); // show loading

                IsWeightListingVisible = false;
                PlaceholderText = Constants.Strings.GraphPage_PlaceholderText_Loading; // defaults to 'loading..' as it is seen briefly

                // do async data read                                
                var todaysWeightEntry = await DataService.GetWeightEntryForDate(DateTime.Today.Date);
                var goal = await DataService.GetGoal();

                // update the UI color (NOTE:: both goal and todaysWeightEntry could be null)
                var infoForToday = WeightLogicHelpers.GetTodaysDisplayInfo(goal, todaysWeightEntry);
                WindowColorService.ChangeAppBaseColor(infoForToday.ColorToShow);

                // update the listing of weight entries
                var latestWeightEntries = await DataService.GetLatestWeightEntries(Constants.App.HISTORY_WeightEntryMaxCount)
                                                           .ConfigureAwait(false) as List<WeightEntry>;

                IsWeightListingVisible = latestWeightEntries.Count > 0; // will show/hide list and placeholder label        
                PlaceholderText = IsWeightListingVisible ? string.Empty : Constants.Strings.GraphPage_PlaceholderText_NoEntries;

                // create a lookup of success/failure values for weight entries and set on converter (used for showing/hiding checkmark per row)
                Dictionary<WeightEntry, bool> successForDateLookup = new Dictionary<WeightEntry, bool>();
                foreach (var entry in latestWeightEntries)
                {
                    bool success = WeightLogicHelpers.WeightMetGoalOnDate(goal, entry.Date, entry.Weight);
                    successForDateLookup.Add(entry, success);
                }
                CheckmarkVisibilityConverter.SuccessForDateLookup = successForDateLookup;

                // NOTE:: Updates to observable collections must happen on UI thread
                Device.BeginInvokeOnMainThread(() =>
                {
                    LatestWeightEntries = (latestWeightEntries == null) ?
                        LatestWeightEntries = new ObservableCollection<WeightEntry>() :
                        LatestWeightEntries = new ObservableCollection<WeightEntry>(latestWeightEntries);
                });

                RefreshGraphDataModel(latestWeightEntries, goal);
            }
            catch (Exception ex)
            {
                AnalyticsService.TrackFatalError($"{nameof(RefreshFromUserDataAsync)} - an exception occurred.", ex);
                // NOTE:: not showing an error here as this is not in response to user action. potentially should show a non-intrusive error banner
            }
            finally
            {
                DecrementPendingRequestCount(); // hide loading
            }
        }

        void RefreshGraphDataModel(List<WeightEntry> entries, WeightLossGoal goal)
        {
            // TODO:: FUTURE:: does all of this have to be done for each refresh? could the axis's be re-used and just
            // have the data points repopulated?
            var dateRange = WeightLogicHelpers.GetGraphDateRange(entries);
            DateTime dateRangeStart = dateRange.Item1;
            DateTime dateRangeEnd = dateRange.Item2;

            var weightRange = WeightLogicHelpers.GetMinMaxWeightRange(goal, entries, dateRangeStart, dateRangeEnd);
            decimal minGraphWeight = weightRange.Item1;
            decimal maxGraphWeight = weightRange.Item2;

            // OxyPlot
            var plotModel = new PlotModel(); //  { Title = "OxyPlot Demo" };
            plotModel.IsLegendVisible = false;
            plotModel.PlotAreaBorderColor = GRID_LINES_COLOR_BORDER;

            // NOTE:: it is assumed the date will always be the first axes in the AxisChanged wiring/un-wiring as that is used 
            // to adjust line markers to show months instead of weeks at a certain zoom level

            // XAxis - dates
            plotModel.Axes.Add(
                new DateTimeAxis
                {
                    Position = AxisPosition.Bottom,
                    Minimum = DateTimeAxis.ToDouble(dateRangeStart),
                    Maximum = DateTimeAxis.ToDouble(dateRangeEnd),
                    AxislineColor = OxyColors.White,
                    TicklineColor = OxyColors.White,
                    MajorGridlineColor = GRID_LINES_COLOR_MAJOR,
                    MinorGridlineColor = GRID_LINES_COLOR_MINOR,
                    AxislineStyle = LineStyle.Solid,
                    TextColor = OxyColors.White,
                    TickStyle = TickStyle.Outside,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Solid,
                    MinorIntervalType = DateTimeIntervalType.Days,
                    IntervalType = DateTimeIntervalType.Days,
                    MinorStep = WeekScale_MinorStep,
                    MajorStep = WeekScale_MajorStep, // a week
                    StringFormat = WeekScale_AxisFormat, // TODO:: FUTURE:: make swap for some cultures?
                    IsZoomEnabled = true,
                    MinimumRange = Constants.App.Graph_MinDateRangeVisible, // closest zoom in shows at least 5 days
                    MaximumRange = Constants.App.Graph_MaxDateRangeVisible, // furthest zoom out shows at most 1 year
                });

            // YAxis - weights
            plotModel.Axes.Add(
                new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Minimum = (double)minGraphWeight,
                    Maximum = (double)maxGraphWeight,
                    AxislineColor = OxyColors.White,
                    TicklineColor = OxyColors.White,
                    MajorGridlineColor = GRID_LINES_COLOR_MAJOR,
                    MinorGridlineColor = GRID_LINES_COLOR_MINOR,
                    AxislineStyle = LineStyle.Solid,
                    TextColor = OxyColors.White,
                    TickStyle = TickStyle.Outside,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Solid,
                    MinorStep = 1,
                    MajorStep = 5,
                    IsZoomEnabled = true,
                    MinimumRange = Constants.App.Graph_MinWeightRangeVisible, // closest zoom in shows at least 5 pounds
                    MaximumRange = Constants.App.Graph_MaxWeightRangeVisible // furthest zoom out shows at most 100 pounds
                });

            var series1 = new LineSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 4,
                MarkerStroke = OxyColors.White,
                MarkerFill = OxyColors.White,
                StrokeThickness = 4,
                MarkerStrokeThickness = 1,
                Color = OxyColors.White
            };

            foreach (var entry in entries)
            {
                series1.Points.Add(new DataPoint(DateTimeAxis.ToDouble(entry.Date), Decimal.ToDouble(entry.Weight)));
            }

            plotModel.Series.Clear();
            plotModel.Series.Add(series1);

            // setup goal line
            if (goal != null)
            {
                var series2 = new LineSeries
                {
                    MarkerType = MarkerType.None,
                    LineStyle = LineStyle.Dash,
                    Color = OxyColors.White
                };

                // diet line

                // we want to extend the goal line at least 30 days past the end of the goal and extend it further if they are already past
                // the goal date (ex: they are 90 days past goal end date and just using the line for maintenance)
                var goalExtendedDate = goal.GoalDate + TimeSpan.FromDays(30);
                if (DateTime.Today.Date > goalExtendedDate)
                    goalExtendedDate = DateTime.Today.Date + TimeSpan.FromDays(30);

                series2.Points.Add(new DataPoint(DateTimeAxis.ToDouble(goal.StartDate - TimeSpan.FromDays(30)), (double)goal.StartWeight));
                series2.Points.Add(new DataPoint(DateTimeAxis.ToDouble(goal.StartDate), (double)goal.StartWeight));
                series2.Points.Add(new DataPoint(DateTimeAxis.ToDouble(goal.GoalDate), (double)goal.GoalWeight));
                series2.Points.Add(new DataPoint(DateTimeAxis.ToDouble(goalExtendedDate), (double)goal.GoalWeight));

                plotModel.Series.Add(series2);
            }

            UnwireDateAxisChangedEvent(); // unwire any previous event handlers for axis zoom changing
            PlotModel = plotModel;
            PlotModel.Axes[0].AxisChanged += DateAxis_Changed;
        }

        void DateAxis_Changed(object sender, AxisChangedEventArgs e)
        {
            if (e.ChangeType != AxisChangeTypes.Zoom)
                return;

            if (PlotModel == null)
                return;

            var dateAxis = sender as DateTimeAxis;
            if (dateAxis == null)
                return;

            var dateMin = dateAxis.ActualMinimum;
            var dateMax = dateAxis.ActualMaximum;
            var delta = dateMax - dateMin;

            if (delta > YearScale_BreakPoint) // more than 280 days being shown
            {
                dateAxis.MinorStep = YearScale_MinorStep;
                dateAxis.MajorStep = YearScale_MajorStep;
                dateAxis.StringFormat = YearScale_AxisFormat;
            }
            else if (delta > ThirtyDayScale_BreakPoint) // more than 90 days being shown
            {
                dateAxis.MinorStep = ThirtyDayScale_MinorStep;
                dateAxis.MajorStep = ThirtyDayScale_MajorStep;
                dateAxis.StringFormat = ThirtyDayScale_AxisFormat;
            }
            else if (delta > WeekScale_BreakPoint) // more than 7 days being shown
            {
                dateAxis.MinorStep = WeekScale_MinorStep;
                dateAxis.MajorStep = WeekScale_MajorStep;
                dateAxis.StringFormat = WeekScale_AxisFormat;
            }
            else
            {
                dateAxis.MinorStep = DayScale_MinorStep;
                dateAxis.MajorStep = DayScale_MajorStep;
                dateAxis.StringFormat = DayScale_AxisFormat;
            }

            //Debug.WriteLine($"min={dateMin}, max={dateMax}, delta={delta}");
        }

        async void EditEntry(WeightEntry selectedItem)
        {
            AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_LaunchedEditExistingWeight, 1);

            var navParams = new NavigationParameters();
            navParams.Add(nameof(WeightEntry), selectedItem);
            await NavigationService.NavigateAsync($"{nameof(NavigationPage)}/{nameof(WeightEntryPage)}", navParams, useModalNavigation: true);
        }

        async void ConfirmDeleteItem(WeightEntry entry)
        {
            AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_DeleteExistingWeight_Start, 1);

            // show warning that an entry will be deleted, allow them to cancel
            var result = await DialogService.DisplayAlertAsync(Constants.Strings.GraphPage_ConfirmDelete_Title,
                string.Format(Constants.Strings.GraphPage_ConfirmDelete_Message, entry.Date),
                Constants.Strings.GENERIC_OK,
                Constants.Strings.GENERIC_CANCEL);

            if (!result)
            {
                AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_DeleteExistingWeight_Cancelled, 1);
                return;
            }

            AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_DeleteExistingWeight_Confirmed, 1);
            bool deleteSucceeded = true;
            try
            {
                IncrementPendingRequestCount(); // show loading
                deleteSucceeded = await DataService.RemoveWeightEntryForDate(entry.Date);

                if (!deleteSucceeded)
                    AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_DeleteExistingWeight_Failed, 1);
            }
            catch (Exception ex)
            {
                AnalyticsService.TrackFatalError($"{nameof(ConfirmDeleteItem)} threw an exception", ex);
                AnalyticsService.TrackEvent(Constants.Analytics.GraphAndListCategory, Constants.Analytics.GraphList_DeleteExistingWeight_Exception, 1);

                deleteSucceeded = false; // handled with dialog below
            }
            finally
            {
                DecrementPendingRequestCount(); // hide loading
            }

            if (!deleteSucceeded)
            {
                await DialogService.DisplayAlertAsync(Constants.Strings.GraphPage_DeleteFailed_Title, Constants.Strings.GraphPage_DeleteFailed_Message, Constants.Strings.GENERIC_OK);
                this.RefreshFromUserDataAsync();
            }
        }
    }
}