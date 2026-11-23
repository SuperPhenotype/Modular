using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod.io search tab. Searches one game at a time, picked by name.
/// </summary>
public partial class ModIoSearchViewModel : ViewModelBase
{
    private const int PageSize = 50;

    private readonly ModIoBackend? _backend;
    private readonly Services.TrackingCoordinator? _tracking;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Pick a game, then search, or leave the search empty to browse";

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _searchResults = new();

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>mod.io's orders. It has no relevance ranking: "Most popular" is subscriber count.</summary>
    private static readonly IReadOnlyList<SortChoice> Sorts =
    [
        new("Most popular", ModSortOrder.Relevance),
        new("Most downloads", ModSortOrder.Downloads),
        new("Highest rated", ModSortOrder.Endorsements),
        new("Recently updated", ModSortOrder.Updated),
        new("Recently added", ModSortOrder.Added),
    ];

    public IReadOnlyList<SortChoice> SortChoices => Sorts;

    [ObservableProperty]
    private SortChoice _selectedSort = Sorts[0];

    [ObservableProperty]
    private bool _adultContentOnly;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private bool _hasPreviousPage;

    partial void OnSelectedSortChanged(SortChoice value) => _ = ExecuteSearchAsync();

    partial void OnAdultContentOnlyChanged(bool value) => _ = ExecuteSearchAsync();

    /// <summary>The game to search; mod.io searches one game at a time.</summary>
    public GamePickerViewModel Game { get; }

    // Designer constructor
    public ModIoSearchViewModel()
    {
        Game = new GamePickerViewModel();
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "4256380",
            Name = "Sample mod.io Search Result",
            Author = "TestAuthor",
            BackendId = BackendIds.ModIo,
            GameDomain = "6715"
        }));
    }

    // DI constructor
    public ModIoSearchViewModel(ModIoBackend backend, AppSettings settings, Services.TrackingCoordinator tracking)
    {
        _backend = backend;
        _tracking = tracking;
        Game = new GamePickerViewModel(
            (name, ct) => backend.SearchGamesAsync(name, ct: ct),
            settings.ModIoGameIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Game.GameChosen += (_, _) => _ = ExecuteSearchAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _debounceTimer?.Dispose();
        var delay = string.IsNullOrWhiteSpace(value) ? 0 : 400;
        _debounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ExecuteSearchAsync()),
            null, delay, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        CurrentPage = 1;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasNextPage) return;
        CurrentPage++;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await LoadPageAsync();
    }

    // Empty search text browses the game's mods in the chosen order.
    private async Task LoadPageAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "mod.io backend not available";
            return;
        }

        if (!Game.HasText)
        {
            SearchResults.Clear();
            TotalResults = 0;
            HasNextPage = HasPreviousPage = false;
            StatusMessage = "Pick a game: mod.io searches one game at a time";
            return;
        }

        IsLoading = true;

        try
        {
            StatusMessage = $"Looking up \"{Game.Text.Trim()}\" on mod.io...";
            var game = await Game.ResolveAsync();
            if (game == null)
            {
                SearchResults.Clear();
                TotalResults = 0;
                HasNextPage = HasPreviousPage = false;
                StatusMessage = $"No mod.io game is named \"{Game.Text.Trim()}\". Pick one from the list.";
                return;
            }

            var terms = SearchText.Trim();
            StatusMessage = terms.Length == 0
                ? $"Listing {game.Name} mods on mod.io ({SelectedSort.Label.ToLowerInvariant()})..."
                : $"Searching mod.io for \"{terms}\" in {game.Name}...";

            var result = await _backend.SearchModsAsync(new ModSearchQuery
            {
                Terms = terms,
                GameDomain = game.Id,
                SortBy = SelectedSort.Order,
                Page = CurrentPage,
                PageSize = PageSize,
                AdultContent = AdultContentOnly
            });

            // With terms and the default order, float the closest name matches to the top
            // without dropping anything the API matched on description or tags. Any other
            // order is the user's choice and stays as mod.io gave it.
            IEnumerable<BackendMod> ordered = terms.Length > 0 && SelectedSort.Order == ModSortOrder.Relevance
                ? result.Mods
                    .Select(mod => (mod, score: FuzzyMatcher.Score(terms, mod.Name)))
                    .OrderByDescending(x => x.score)
                    .Select(x => x.mod)
                : result.Mods;

            SearchResults.Clear();
            foreach (var mod in ordered)
            {
                var display = new ModDisplayModel(mod);
                display.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ModDisplayModel.IsSelected))
                        UpdateSelectedCount();
                };
                SearchResults.Add(display);
            }

            TotalResults = result.TotalCount;
            HasNextPage = result.HasNextPage;
            HasPreviousPage = CurrentPage > 1;
            UpdateSelectedCount();
            StatusMessage = result.TotalCount == 0
                ? terms.Length == 0 ? $"No mods listed for {game.Name}" : $"No mods found for \"{terms}\""
                : $"{result.TotalCount} mod(s) on mod.io (page {CurrentPage})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SubscribeSelectedAsync()
    {
        if (_backend == null || _tracking == null)
            return;

        var selected = GetSelectedMods().Select(m => (m.ModId, (string?)m.GameDomain)).ToList();
        IsLoading = true;
        try
        {
            StatusMessage = await _tracking.ApplyAsync(_backend, selected, track: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var mod in SearchResults)
            mod.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var mod in SearchResults)
            mod.IsSelected = false;
        UpdateSelectedCount();
    }

    public void UpdateSelectedCount() => SelectedCount = SearchResults.Count(m => m.IsSelected);

    public IEnumerable<ModDisplayModel> GetSelectedMods() => SearchResults.Where(m => m.IsSelected);
}
