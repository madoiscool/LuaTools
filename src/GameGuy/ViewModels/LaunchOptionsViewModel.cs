using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameGuy.Services;
using GameGuy.Services.AppInfo;

namespace GameGuy.ViewModels;

/// <summary>Bindable wrapper around a <see cref="LaunchOption"/>. The model is a plain POCO because it
/// also gets serialized into the mod store.</summary>
public partial class LaunchEntryViewModel : ObservableObject
{
    public string Index { get; set; }
    public string SourceIndex { get; set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Title))] private string _executable;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Subtitle))] private string _arguments;
    [ObservableProperty] private string _workingDir;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Title))] private string _description;
    [ObservableProperty] private string _type;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Subtitle))] private string _osList;
    [ObservableProperty] private string _osArch;
    [ObservableProperty] private string _betaKey;
    [ObservableProperty] private string _ownsDlc;

    /// <summary>List label: the description if Steam gave one, else the executable.</summary>
    public string Title => string.IsNullOrWhiteSpace(Description) ? Executable : Description;

    public string Subtitle => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(Executable) ? null : Executable,
        string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
        string.IsNullOrWhiteSpace(OsList) ? null : OsList,
    }.Where(s => s is not null));

    public LaunchEntryViewModel(LaunchOption option)
    {
        Index = option.Index;
        SourceIndex = option.SourceIndex;
        _executable = option.Executable;
        _arguments = option.Arguments;
        _workingDir = option.WorkingDir;
        _description = option.Description;
        _type = option.Type;
        _osList = option.OsList;
        _osArch = option.OsArch;
        _betaKey = option.BetaKey;
        _ownsDlc = option.OwnsDlc;
    }

    public LaunchOption ToOption() => new()
    {
        Index = Index,
        SourceIndex = SourceIndex,
        Executable = Executable.Trim(),
        Arguments = Arguments.Trim(),
        WorkingDir = WorkingDir.Trim(),
        Description = Description.Trim(),
        Type = Type.Trim(),
        OsList = OsList.Trim(),
        OsArch = OsArch.Trim(),
        BetaKey = BetaKey.Trim(),
        OwnsDlc = OwnsDlc.Trim(),
    };
}

/// <summary>
/// The Launch-options editor: read a game's entries out of Steam's appinfo cache, edit them, and stage
/// the result. Writing appinfo is a separate confirmed step because it has to close Steam.
/// </summary>
public partial class LaunchOptionsViewModel : ObservableObject
{
    private readonly LaunchOptionsService _launch;
    private readonly ToastService _toast;

    private LaunchState? _state;

    /// <summary>What the entry list looked like when it was loaded, in the exact shape a save would
    /// produce, so comparing against it answers "would saving change anything?".</summary>
    private List<LaunchOption> _baseline = [];

    /// <summary>Entries we've hooked <see cref="INotifyPropertyChanged"/> on, so field edits (not just
    /// add/delete/reorder) mark the dialog dirty.</summary>
    private readonly List<LaunchEntryViewModel> _watched = [];

    public ObservableCollection<LaunchEntryViewModel> Entries { get; } = [];

    public ObservableCollection<string> TypeOptions { get; } =
        ["default", "none", "option1", "option2", "vr"];
    public ObservableCollection<string> OsOptions { get; } =
        ["windows", "macos", "linux", ""];

    /// <summary>Common branch names, by frequency across 3,575 real entries that use BetaKey. It's a
    /// free-text BRANCH NAME (1,794 distinct values in the wild), not a yes/no, hence an editable
    /// combo rather than a checkbox.</summary>
    public ObservableCollection<string> BranchOptions { get; } =
        ["", "beta", "default", "development", "test", "legacy", "steam_legacy", "experimental"];

    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private long _appId;
    [ObservableProperty] private string? _installPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LaunchEntryViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>True once this game has a stored edit. Enables Restore.</summary>
    [ObservableProperty] private bool _isModded;

    /// <summary>True when the entry list no longer matches <see cref="_baseline"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    /// <summary>Set by the view so the dialog can close itself.</summary>
    public Action<bool>? CloseWith { get; set; }

    public LaunchOptionsViewModel(LaunchOptionsService launch, ToastService toast)
    {
        _launch = launch;
        _toast = toast;
        Entries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>
    /// Re-derive the whole watch set on every change rather than tracking adds and removes: a
    /// <see cref="Entries"/> Clear() raises Reset with no OldItems, so there'd be nothing to unsubscribe
    /// from and each reload would leak a duplicate handler onto the old entries. The list is a handful
    /// of items, so rebuilding it costs nothing.
    /// </summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var entry in _watched) entry.PropertyChanged -= OnEntryEdited;
        _watched.Clear();
        foreach (var entry in Entries)
        {
            entry.PropertyChanged += OnEntryEdited;
            _watched.Add(entry);
        }
        RecomputeDirty();
    }

    private void OnEntryEdited(object? sender, PropertyChangedEventArgs e) => RecomputeDirty();

    /// <summary>
    /// Both sides are renumbered before comparing, so an app whose indices already had gaps (1,892 real
    /// apps do) doesn't read as dirty the instant it opens just because a save would compact them.
    /// </summary>
    private void RecomputeDirty()
    {
        var current = Entries.Select(e => e.ToOption()).ToList();
        LaunchOption.Renumber(current);
        IsDirty = LaunchModStore.Differs(_baseline, current);
    }

    /// <summary>Load a game's launch entries. Indexing the 373 MB cache takes a couple of seconds, so
    /// this runs off the UI thread.</summary>
    public async Task LoadAsync(long appId, string gameName)
    {
        AppId = appId;
        GameName = gameName;
        IsLoading = true;
        Error = null;

        try
        {
            var state = await Task.Run(() => _launch.Read((int)appId));
            if (state is null)
            {
                Error = Resources.Strings.Launch_NotFound;
                return;
            }

            _state = state;
            OnPropertyChanged(nameof(CanSave));
            InstallPath = state.InstallDir;
            IsModded = state.IsModded;

            // Show the staged edit if there is one: that's what the user last asked for, even if Steam
            // has since overwritten the live file.
            var source = _launch.Store.Get((int)appId)?.Desired ?? state.Current;
            Entries.Clear();
            foreach (var option in source) Entries.Add(new LaunchEntryViewModel(option));
            Selected = Entries.FirstOrDefault();

            // Baseline comes back out through ToOption() rather than from `source` directly, so it's
            // identical to what a save would write. Otherwise trimming alone would read as an edit.
            _baseline = Entries.Select(e => e.ToOption()).ToList();
            LaunchOption.Renumber(_baseline);
            IsDirty = false;
        }
        catch (AppInfoFormatException ex)
        {
            Error = string.Format(Resources.Strings.Launch_UnsupportedFormat, ex.Message);
        }
        catch (Exception ex)
        {
            Error = string.Format(Resources.Strings.Launch_ReadFailed, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── entry list ──────────────────────────────────────────────────

    [RelayCommand]
    private void AddEntry()
    {
        var option = new LaunchOption
        {
            Index = LaunchOption.NextIndex(Entries.Select(e => e.ToOption())),
            Executable = "",
            Type = "default",
            OsList = "windows",
        };
        var vm = new LaunchEntryViewModel(option);
        Entries.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private void DeleteEntry()
    {
        if (Selected is not { } entry) return;
        int at = Entries.IndexOf(entry);
        Entries.Remove(entry);
        Selected = Entries.Count == 0 ? null : Entries[Math.Min(at, Entries.Count - 1)];
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    /// <summary>
    /// Reorder within the list. This only becomes durable because <see cref="Save"/> renumbers the keys
    /// 0,1,2… in list order. Steam takes the running order from the <c>config.launch</c> KEY, not from
    /// the order entries happen to sit in the file, so moving an entry while keeping its original key
    /// would change nothing outside this dialog. (SteamEdit does the same: it reads entries
    /// <c>OrderBy(key)</c> and rewrites them as <c>Count.ToString()</c>.)
    /// </summary>
    private void Move(int delta)
    {
        if (Selected is not { } entry) return;
        int from = Entries.IndexOf(entry);
        int to = from + delta;
        if (to < 0 || to >= Entries.Count) return;
        Entries.Move(from, to);
        Selected = entry;
    }

    // ── save / restore ──────────────────────────────────────────────

    /// <summary>Save is only offered when there's an actual change to write. Applying closes Steam, so
    /// a no-op save is worse than useless.</summary>
    public bool CanSave => _state is not null && Error is null && IsDirty;

    /// <summary>Stage the edit, then offer to write it into appinfo (which closes Steam).</summary>
    [RelayCommand]
    private void Save()
    {
        if (_state is null) return;

        var desired = Entries.Select(e => e.ToOption()).ToList();
        LaunchOption.Renumber(desired);   // list order becomes 0,1,2… so Up/Down actually applies
        if (desired.Any(o => string.IsNullOrWhiteSpace(o.Executable)))
        {
            Error = Resources.Strings.Launch_ExecutableRequired;
            return;
        }

        _launch.Stage((int)AppId, _state.ChangeNumber, _state.Current, desired);
        IsModded = true;

        if (Confirm(Resources.Strings.Launch_ApplyNow_Body, Resources.Strings.Launch_ApplyNow_Title))
            ApplyToSteam(new Dictionary<int, IReadOnlyList<LaunchOption>> { [(int)AppId] = desired });
        else
            _toast.Show(Resources.Strings.Launch_Title, Resources.Strings.Launch_Staged);

        CloseWith?.Invoke(true);
    }

    /// <summary>Put back the snapshot taken before this game was first edited.</summary>
    [RelayCommand]
    private void Restore()
    {
        if (_launch.StageRestore((int)AppId) is not { } original)
        {
            _toast.Show(Resources.Strings.Launch_Title, Resources.Strings.Launch_NothingToRestore, error: true);
            return;
        }

        if (!Confirm(Resources.Strings.Launch_Restore_Body, Resources.Strings.Launch_Restore_Title)) return;

        ApplyToSteam(new Dictionary<int, IReadOnlyList<LaunchOption>> { [(int)AppId] = original });
        _launch.Store.Remove((int)AppId);
        IsModded = false;
        CloseWith?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseWith?.Invoke(false);

    /// <summary>
    /// Write into appinfo.vdf. Steam is closed first. It owns the file and regenerates it from its own
    /// in-memory state, so writing underneath a running client achieves nothing.
    /// </summary>
    private void ApplyToSteam(IReadOnlyDictionary<int, IReadOnlyList<LaunchOption>> edits)
    {
        var result = _launch.Apply(edits, restartSteam: true);
        if (result.Ok)
            _toast.Show(Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? Resources.Strings.Launch_Applied_Restarted
                    : Resources.Strings.Launch_Applied);
        else
            _toast.Show(Resources.Strings.Launch_Title,
                string.Format(Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    private static bool Confirm(string body, string title) =>
        MessageBox.Show(body, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
