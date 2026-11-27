using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameGuy.ViewModels;

/// <summary>
/// Shared list-page machinery for the Manage and Fixes pages: a filtered master list sliced into a
/// paged, virtualized grid (results-per-page dropdown, windowed page-number pager, prev/next,
/// persisted page size, scroll-to-top) plus a cooldown-guarded refresh helper. Subclasses build the
/// filtered+sorted list and hand it to <see cref="SetFiltered"/>; everything paging-related lives here.
/// </summary>
public abstract partial class PagedListViewModel<T> : ObservableObject
{
    /// <summary>Filtered + sorted full list; the page slice is taken from this.</summary>
    protected List<T> _filtered = [];

    private DateTime _lastRefresh;    // refresh-button cooldown
    private int _filteredCount;       // count of the filtered list (drives TotalPages)

    // When true, a CurrentPage change won't re-slice. Used while SetFiltered resets/clamps the page,
    // since it does its own single slice at the end (avoids slicing the stale _filtered list twice).
    private bool _suppressPageSlice;

    /// <summary>Set by the view to scroll the grid back to the top (on page change).</summary>
    public Action? ScrollToTop { get; set; }

    /// <summary>The current page of filtered items. Shown in the virtualized grid. (When the page size
    /// is "All" this holds the entire filtered list, i.e. a single infinite-scroll behaviour.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(ShowItems))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ObservableCollection<T> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(ShowItems))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string _emptyMessage = "";

    // Single source of truth: the grid shows iff there are items; "empty" only when a load finished
    // with no results. Cards render only when not loading. Keeps the spinner from co-rendering over
    // the grid.
    public bool HasItems => Items.Count > 0;
    public bool ShowItems => HasItems && !IsLoading;
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    // ── Pagination ───────────────────────────────────────────────────
    public const string AllPages = "All";

    /// <summary>Results-per-page choices. "All" = single infinite scroll (no slicing).</summary>
    public ObservableCollection<string> PageSizeOptions { get; } = ["12", "24", "48", AllPages];

    /// <summary>Selected page-size label ("12" | "24" | "48" | "All"). Persisted via SavePageSizeSetting.</summary>
    [ObservableProperty] private string _selectedPageSize = "24";

    /// <summary>Numeric page size; 0 = "All" (no pagination).</summary>
    public int PageSize => SelectedPageSize == AllPages ? 0 : int.Parse(SelectedPageSize);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPage = 1;

    public int TotalPages => PageSize == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_filteredCount / (double)PageSize));

    /// <summary>Page-number buttons for the pager. Windowed (1 … n-1 n n+1 … last) when there are many
    /// pages; 0 is a sentinel for an ellipsis gap.</summary>
    public ObservableCollection<int> PageNumbers { get; } = [];

    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;

    /// <summary>The pager (numbers + prev/next) shows only when paginating and there's more than one page.</summary>
    public bool ShowPager => PageSize != 0 && TotalPages > 1;

    public string PageLabel => string.Format(Resources.Strings.Manage_PageLabel, CurrentPage, TotalPages);

    partial void OnSelectedPageSizeChanged(string value)
    {
        SavePageSizeSetting(PageSize);
        // Page size changed the pagination, not the filter. Reset to page 1 and re-slice the existing
        // _filtered list (no re-filter needed).
        _suppressPageSlice = true;
        CurrentPage = 1;
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ShowPager));
        _suppressPageSlice = false;
        ApplyPageSlice();
    }

    partial void OnCurrentPageChanged(int value) { if (!_suppressPageSlice) ApplyPageSlice(); }

    [RelayCommand]
    private void PrevPage() { if (CanGoPrev) CurrentPage--; }

    [RelayCommand]
    private void NextPage() { if (CanGoNext) CurrentPage++; }

    [RelayCommand]
    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages || page == CurrentPage) return;
        CurrentPage = page;
    }

    /// <summary>Seed the page-size from a persisted value without triggering a save/slice (call from the
    /// subclass constructor). 0 = "All".</summary>
    protected void InitPageSize(int persisted) =>
        // Set the backing field directly so OnSelectedPageSizeChanged doesn't fire during construction
        // (which would re-save the setting and slice an empty list). MVVMTK0034 is expected/intended here.
#pragma warning disable MVVMTK0034
        _selectedPageSize = persisted == 0 ? AllPages : persisted.ToString();
#pragma warning restore MVVMTK0034

    /// <summary>Persist the chosen page size (0 = "All"). Overridden by subclasses that have settings.</summary>
    protected virtual void SavePageSizeSetting(int size) { }

    /// <summary>Hook fired after the visible page is (re)built, e.g. to warm the slice's cover images.
    /// Default is a no-op.</summary>
    protected virtual void OnPageSliced(IReadOnlyList<T> slice) { }

    /// <summary>Replace the filtered+sorted master list and render its current page. This is the single
    /// entry point subclasses call after (re)building their filter result.</summary>
    /// <param name="resetPage">True (default) for a user-initiated filter/search/sort change. Jump back
    /// to page 1. False for passive re-renders so the user stays on their current page; the page is still
    /// clamped into range.</param>
    protected void SetFiltered(IEnumerable<T> filtered, bool resetPage = true)
    {
        _suppressPageSlice = true;
        if (resetPage) CurrentPage = 1;

        _filtered = filtered as List<T> ?? filtered.ToList();
        _filteredCount = _filtered.Count;

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ShowPager));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages; // still suppressed → no slice here
        _suppressPageSlice = false;
        ApplyPageSlice();
    }

    /// <summary>Render the current page from <see cref="_filtered"/> into <see cref="Items"/>. With page
    /// size "All" this shows the whole filtered list. Re-slices only (never re-filters), so it's cheap
    /// to call on page changes.</summary>
    private void ApplyPageSlice()
    {
        var slice = PageSize == 0
            ? _filtered
            : _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();

        Items = new ObservableCollection<T>(slice);
        RebuildPageNumbers();
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        ScrollToTop?.Invoke();
        OnPageSliced(slice);
    }

    /// <summary>Build the windowed page-number list for the pager (0 = ellipsis gap). Shows every page
    /// when there are ≤9; otherwise "1 … c-1 c c+1 … last".</summary>
    private void RebuildPageNumbers()
    {
        PageNumbers.Clear();
        int total = TotalPages, cur = CurrentPage;
        if (PageSize == 0 || total <= 1) return;

        void Add(int n) => PageNumbers.Add(n);
        if (total <= 9)
        {
            for (int i = 1; i <= total; i++) Add(i);
            return;
        }

        Add(1);
        int start = Math.Max(2, cur - 1), end = Math.Min(total - 1, cur + 1);
        if (start > 2) Add(0);                 // gap after 1
        for (int i = start; i <= end; i++) Add(i);
        if (end < total - 1) Add(0);           // gap before last
        Add(total);
    }

    /// <summary>Run <paramref name="reload"/> guarded by a 1s cooldown + in-flight (<see cref="IsLoading"/>)
    /// check, so rapid Refresh clicks can't spawn concurrent re-scans.</summary>
    protected async Task RefreshWithCooldownAsync(Func<Task> reload)
    {
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(1) || IsLoading) return;
        _lastRefresh = DateTime.UtcNow;
        await reload();
    }
}
