﻿using LineDietXF.Interfaces;
using LineDietXF.Types;
using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LineDietXF.Services
{
    /// <summary>
    /// Asynchronous SQLite implementation of IDataService
    /// </summary>
    public class SQLiteDataService : IDataService
    {
        public bool HasBeenInitialized { get; private set; }

        IAnalyticsService AnalyticsService { get; set; }
        SQLiteAsyncConnection _connection;

        public event EventHandler UserDataUpdated;

        public SQLiteDataService(IAnalyticsService analyticsService)
        {
            AnalyticsService = analyticsService;

            HasBeenInitialized = false;
        }

        public async Task Initialize(string dbPath)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif
            _connection = new SQLiteAsyncConnection(dbPath);
            await _connection.CreateTableAsync<WeightEntry>();
            await _connection.CreateTableAsync<WeightLossGoal>();

            HasBeenInitialized = true;
            FireUserDataUpdated();
        }

        public async Task<bool> AddWeightEntry(WeightEntry newEntry)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            // weight already exists, should instead delete old weight and then re-add
            if (await WeightEntryForDateExists(newEntry.Date))
                return false;

            int result = await _connection.InsertAsync(newEntry);
            if (result != 1)
            {
                AnalyticsService.TrackFatalError($"{nameof(AddWeightEntry)} got an unexpected result (not 1) of {result}", null);
                return false;
            }

            FireUserDataUpdated();

            return true;
        }

        public async Task<IList<WeightEntry>> GetAllWeightEntries()
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif
            var entries = await _connection.Table<WeightEntry>().ToListAsync();
            return entries;
        }

        public async Task<IList<WeightEntry>> GetLatestWeightEntries(int maxCount)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif
            var allEntries = await GetAllWeightEntries();
            var limitedSet = allEntries.OrderByDescending(x => x.Date).Take(maxCount).ToList();
            return limitedSet;
        }

        public async Task<WeightEntry> GetWeightEntryForDate(DateTime dt)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif
            // NOTE:: this compares both the date and time (not just the date), this is ok as we always store date only without time
            var entry = await _connection.Table<WeightEntry>()
                .Where(w => w.Date == dt.Date).FirstOrDefaultAsync();

            return entry;
        }

        public async Task<bool> RemoveWeightEntryForDate(DateTime dt)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            var entry = await GetWeightEntryForDate(dt);
            if (entry == null)
            {
                FireUserDataUpdated();
                return false;
            }

            var result = await _connection.DeleteAsync(entry);
            if (result != 1)
            {
                FireUserDataUpdated();
                return false;
            }

            FireUserDataUpdated();

            return true;
        }

        public async Task<WeightLossGoal> GetGoal()
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            var goal = await _connection.Table<WeightLossGoal>().Where(x => true).FirstOrDefaultAsync(); // there is only one row in this table
            return goal;
        }

        public async Task<bool> SetGoal(WeightLossGoal weightLossGoal)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            // delete anything existing goal
            if (!await RemoveGoal())
            {
                FireUserDataUpdated();
                return false;
            }
                        
            var result = await _connection.InsertAsync(weightLossGoal);
            if (result != 1)
            {
                FireUserDataUpdated();
                return false;
            }

            FireUserDataUpdated();
            return true;
        }

        public async Task<bool> RemoveGoal()
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            var goal = await GetGoal();
            if (goal != null)
            {
                var result = await _connection.DeleteAsync(goal);
                if (result != 1)
                {
                    AnalyticsService.TrackFatalError($"{nameof(SetGoal)} got an unexpected result (not 1) of {result}", null);
                    return false;
                }
            }
            FireUserDataUpdated();

            return true;
        }

        public async Task<bool> WeightEntryForDateExists(DateTime dt)
        {
#if DEBUG
            await SimulateSlowNetworkIfEnabled();
#endif

            var entry = await GetWeightEntryForDate(dt);
            return entry != null;
        }

        void FireUserDataUpdated()
        {
            UserDataUpdated?.Invoke(this, null);
        }

        async Task SimulateSlowNetworkIfEnabled()
        {
#if DEBUG
            if (Constants.App.DEBUG_SIMULATE_SLOW_RESPONSE)
                await Task.Delay(Constants.App.DEBUG_SIMULATE_SLOW_RESPONSE_TIME);
#endif
        }
    }
}
