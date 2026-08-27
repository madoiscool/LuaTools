using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.SAM;

namespace LuaToolsGui.ViewModels;

public enum AchievementFilter
{
    All,
    Unlocked,
    Locked,
}

public enum GameCategoryFilter
{
    All,
    Installed,
    Normal,
    Demos,
    Mods,
}

public partial class AchievementsViewModel : ObservableObject
{
    private readonly SamService _samService;
    private readonly ToastService _toastService;
    private readonly CoverCache _coverCache;

    private readonly object _gamesLock = new();
    private readonly object _achsLock = new();
    private readonly object _statsLock = new();

    public AchievementsViewModel(
        SamService samService,
        ToastService toastService,
        CoverCache coverCache)
    {
        _samService = samService;
        _toastService = toastService;
        _coverCache = coverCache;

        BindingOperations.EnableCollectionSynchronization(AllGames, _gamesLock);
        BindingOperations.EnableCollectionSynchronization(Achievements, _achsLock);
        BindingOperations.EnableCollectionSynchronization(Stats, _statsLock);

        _gamesView = CollectionViewSource.GetDefaultView(AllGames);
        _gamesView.Filter = FilterGame;

        _achievementsView = CollectionViewSource.GetDefaultView(Achievements);
        _achievementsView.Filter = FilterAchievement;

        _statsView = CollectionViewSource.GetDefaultView(Stats);
        _statsView.Filter = FilterStat;
    }

    // ── Collections & Views ──────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<SamGameInfo> _allGames = [];

    [ObservableProperty]
    private ObservableCollection<SamAchievement> _achievements = [];

    [ObservableProperty]
    private ObservableCollection<SamStat> _stats = [];

    private ICollectionView _gamesView;
    private ICollectionView _achievementsView;
    private ICollectionView _statsView;

    public ICollectionView FilteredGames => _gamesView;
    public ICollectionView FilteredAchievements => _achievementsView;
    public ICollectionView FilteredStats => _statsView;

    // ── Navigation & View State ──────────────────────────────────────
    [ObservableProperty]
    private bool _isGameSelected;

    [ObservableProperty]
    private SamGameInfo? _selectedGame;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    // ── Game Picker Search & Filters ─────────────────────────────────
    [ObservableProperty]
    private string _gameSearchText = string.Empty;

    [ObservableProperty]
    private GameCategoryFilter _selectedCategoryFilter = GameCategoryFilter.All;

    [ObservableProperty]
    private string _customAppIdInput = string.Empty;

    // ── Achievement Management State ─────────────────────────────────
    [ObservableProperty]
    private string _achievementSearchText = string.Empty;

    [ObservableProperty]
    private AchievementFilter _selectedAchievementFilter = AchievementFilter.All;

    [ObservableProperty]
    private int _selectedTabIndex; // 0 = Achievements, 1 = Statistics

    [ObservableProperty]
    private bool _enableStatsEditing;

    [ObservableProperty]
    private int _unlockedCount;

    // Message shown when selected game has no achievements
    [ObservableProperty]
    private string _noAchievementsMessage = string.Empty;

    [ObservableProperty]
    private int _totalAchievementsCount;

    [ObservableProperty]
    private double _progressPercentage;

    public bool HasPendingChanges =>
        Achievements.Any(a => a.IsModified) ||
        Stats.Any(s => s.IsModified);

    // ── Search & Filter Logic ────────────────────────────────────────

    partial void OnGameSearchTextChanged(string value) => _gamesView.Refresh();
    partial void OnSelectedCategoryFilterChanged(GameCategoryFilter value) => _gamesView.Refresh();

    private bool FilterGame(object item)
    {
        if (item is not SamGameInfo game) return false;

        // Category filter
        switch (SelectedCategoryFilter)
        {
            case GameCategoryFilter.Installed:
                if (!game.IsInstalledInLuaTools) return false;
                break;
            case GameCategoryFilter.Normal:
                if (!string.Equals(game.Type, "normal", StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case GameCategoryFilter.Demos:
                if (!string.Equals(game.Type, "demo", StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case GameCategoryFilter.Mods:
                if (!string.Equals(game.Type, "mod", StringComparison.OrdinalIgnoreCase)) return false;
                break;
        }

        // Text search
        if (!string.IsNullOrWhiteSpace(GameSearchText))
        {
            string q = GameSearchText.Trim();
            if (uint.TryParse(q, out uint searchAppId) && game.Id == searchAppId)
                return true;

            return game.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    partial void OnAchievementSearchTextChanged(string value) => _achievementsView.Refresh();
    partial void OnSelectedAchievementFilterChanged(AchievementFilter value) => _achievementsView.Refresh();

    private bool FilterAchievement(object item)
    {
        if (item is not SamAchievement ach) return false;

        // State filter
        switch (SelectedAchievementFilter)
        {
            case AchievementFilter.Unlocked:
                if (!ach.IsAchieved) return false;
                break;
            case AchievementFilter.Locked:
                if (ach.IsAchieved) return false;
                break;
        }

        // Text search
        if (!string.IsNullOrWhiteSpace(AchievementSearchText))
        {
            string q = AchievementSearchText.Trim();
            return ach.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   ach.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   ach.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private bool FilterStat(object item)
    {
        if (item is not SamStat stat) return false;

        if (!string.IsNullOrWhiteSpace(AchievementSearchText))
        {
            string q = AchievementSearchText.Trim();
            return stat.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   stat.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private bool _isBulkUpdating;

    private void RecalculateProgress()
    {
        if (_isBulkUpdating) return;

        TotalAchievementsCount = Achievements.Count;
        UnlockedCount = Achievements.Count(a => a.IsAchieved);
        ProgressPercentage = TotalAchievementsCount > 0
            ? (double)UnlockedCount / TotalAchievementsCount * 100.0
            : 0.0;

        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    // ── Commands & Actions ───────────────────────────────────────────

    [RelayCommand]
    public async Task LoadGamesAsync(bool force = false)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Scanning Steam games...";

        try
        {
            var games = await _samService.GetGamesAsync(force);
            OnUi(() =>
            {
                AllGames = new ObservableCollection<SamGameInfo>(games);
                BindingOperations.EnableCollectionSynchronization(AllGames, _gamesLock);
                _gamesView = CollectionViewSource.GetDefaultView(AllGames);
                _gamesView.Filter = FilterGame;
                OnPropertyChanged(nameof(FilteredGames));
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load games: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    public async Task OpenCustomAppIdAsync()
    {
        if (uint.TryParse(CustomAppIdInput.Trim(), out uint appId) && appId > 0)
        {
            CustomAppIdInput = string.Empty;
            await SelectGameAsync(new SamGameInfo
            {
                Id = appId,
                Name = $"App {appId}",
                Type = "normal",
                DisplayCoverUrl = _coverCache.GetCoverPathOrUrl(appId),
            });
        }
        else
        {
            _toastService.Show("Achievements", "Please enter a valid numeric Steam App ID.", error: true);
        }
    }

    [RelayCommand]
    public async Task SelectGameAsync(SamGameInfo? game)
    {
        if (game == null) return;

        SelectedGame = game;
        IsGameSelected = true;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = $"Loading achievements for {game.Name}...";

        OnUi(() =>
        {
            Achievements = [];
            Stats = [];
        });

        try
        {
            var data = await _samService.GetGameStatsAsync(game.Id, forceRefresh: true);

            if (!string.IsNullOrEmpty(data.ErrorMessage))
            {
                ErrorMessage = data.ErrorMessage;
                _toastService.Show("Achievements", data.ErrorMessage, error: true);
                // Clear any previous message about missing achievements
                NoAchievementsMessage = string.Empty;
                return;
            }

            if (!string.IsNullOrWhiteSpace(data.GameName))
            {
                game.Name = data.GameName;
                SelectedGame = game;
            }

            // Determine if there are any achievements
            bool hasAchievements = data.Achievements != null && data.Achievements.Count > 0;

            OnUi(() =>
            {
                var newAchievements = new ObservableCollection<SamAchievement>();
                if (hasAchievements)
                {
                    var achList = data.Achievements!;
                    foreach (var ach in achList)
                    {
                        ach.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(SamAchievement.IsAchieved))
                            {
                                RecalculateProgress();
                            }
                        };
                        newAchievements.Add(ach);
                    }
                    NoAchievementsMessage = string.Empty; // clear any previous message
                }
                else
                {
                    // Show a friendly message when no achievements are present
                    NoAchievementsMessage = $"No achievements found for {game.Name}.";
                    _toastService.Show("Achievements", NoAchievementsMessage, error: false);
                }

                var newStats = new ObservableCollection<SamStat>();
                foreach (var stat in data.Stats)
                {
                    stat.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName is nameof(SamStat.IntValue) or nameof(SamStat.FloatValue) or nameof(SamStat.ValueString))
                        {
                            OnPropertyChanged(nameof(HasPendingChanges));
                        }
                    };
                    newStats.Add(stat);
                }

                Achievements = newAchievements;
                Stats = newStats;

                BindingOperations.EnableCollectionSynchronization(Achievements, _achsLock);
                BindingOperations.EnableCollectionSynchronization(Stats, _statsLock);

                _achievementsView = CollectionViewSource.GetDefaultView(Achievements);
                _achievementsView.Filter = FilterAchievement;

                _statsView = CollectionViewSource.GetDefaultView(Stats);
                _statsView.Filter = FilterStat;

                OnPropertyChanged(nameof(FilteredAchievements));
                OnPropertyChanged(nameof(FilteredStats));
                
                RecalculateProgress();
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading stats: {ex.Message}";
            _toastService.Show("Achievements", ErrorMessage, error: true);
        }
        finally
        {
            IsLoading = false;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    public void BackToPicker()
    {
        IsGameSelected = false;
        SelectedGame = null;
        OnUi(() =>
        {
            Achievements = [];
            Stats = [];
            OnPropertyChanged(nameof(FilteredAchievements));
            OnPropertyChanged(nameof(FilteredStats));
        });
        ErrorMessage = null;
        StatusMessage = null;
    }

    [RelayCommand]
    public void UnlockAll()
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var ach in Achievements)
            {
                ach.IsAchieved = true;
            }
        }
        finally
        {
            _isBulkUpdating = false;
            RecalculateProgress();
            _achievementsView.Refresh();
        }
    }

    [RelayCommand]
    public void LockAll()
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var ach in Achievements)
            {
                ach.IsAchieved = false;
            }
        }
        finally
        {
            _isBulkUpdating = false;
            RecalculateProgress();
            _achievementsView.Refresh();
        }
    }

    [RelayCommand]
    public void InvertSelection()
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var ach in Achievements)
            {
                ach.IsAchieved = !ach.IsAchieved;
            }
        }
        finally
        {
            _isBulkUpdating = false;
            RecalculateProgress();
            _achievementsView.Refresh();
        }
    }

    [RelayCommand]
    public async Task ReloadStatsAsync()
    {
        if (SelectedGame == null) return;
        await SelectGameAsync(SelectedGame);
    }

    [RelayCommand]
    public async Task CommitChangesAsync()
    {
        if (SelectedGame == null || IsSaving) return;

        var request = new SamStoreRequest
        {
            AppId = SelectedGame.Id,
        };

        foreach (var ach in Achievements.Where(a => a.IsModified))
        {
            request.Achievements[ach.Id] = ach.IsAchieved;
        }

        foreach (var stat in Stats.Where(s => s.IsModified))
        {
            if (stat.IsFloat)
            {
                request.FloatStats[stat.Id] = stat.FloatValue;
            }
            else
            {
                request.IntStats[stat.Id] = stat.IntValue;
            }
        }

        if (request.Achievements.Count == 0 && request.IntStats.Count == 0 && request.FloatStats.Count == 0)
        {
            _toastService.Show("Achievements", "No modified achievements or stats to store.");
            return;
        }

        IsSaving = true;
        StatusMessage = "Saving changes to Steam Cloud...";

        try
        {
            var result = await _samService.StoreStatsAsync(request);

            if (result.Success)
            {
                // Update original states
                foreach (var ach in Achievements)
                {
                    ach.OriginalIsAchieved = ach.IsAchieved;
                }
                foreach (var stat in Stats)
                {
                    stat.OriginalIntValue = stat.IntValue;
                    stat.OriginalFloatValue = stat.FloatValue;
                }

                OnPropertyChanged(nameof(HasPendingChanges));

                _toastService.Show(
                    "Achievements Saved",
                    $"Successfully updated {result.AchievementsStored} achievements and {result.StatsStored} stats in Steam!");
            }
            else
            {
                string err = result.ErrorMessage ?? "Failed to save stats to Steam.";
                ErrorMessage = err;
                _toastService.Show("Steam Error", err, error: true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _toastService.Show("Error", ex.Message, error: true);
        }
        finally
        {
            IsSaving = false;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    public async Task ResetAllStatsAsync()
    {
        if (SelectedGame == null) return;

        var request = new SamStoreRequest
        {
            AppId = SelectedGame.Id,
            ResetAll = true,
            ResetAchievementsToo = true,
        };

        IsSaving = true;
        StatusMessage = "Resetting all game stats in Steam...";

        try
        {
            var result = await _samService.StoreStatsAsync(request);
            if (result.Success)
            {
                _toastService.Show("Stats Reset", "All stats and achievements have been reset.");
                await SelectGameAsync(SelectedGame);
            }
            else
            {
                _toastService.Show("Reset Failed", result.ErrorMessage ?? "Failed to reset stats.", error: true);
            }
        }
        finally
        {
            IsSaving = false;
            StatusMessage = null;
        }
    }
}
