using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the GameBanana search tab.
/// Searches the GameBanana API and ranks results with fuzzy matching.
/// Limits the search to one game when one is picked by name.
/// </summary>
public partial class GameBananaSearchViewModel : ViewModelBase
{
    private readonly GameBananaBackend? _backend;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Search, or leave the search empty to browse; pick a game to stay within it";

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _searchResults = new();

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>
    /// GameBanana's orders. "Relevance" is its best-match search when there are terms and the
    /// most liked when there are none.
    /// </summary>
    private static readonly IReadOnlyList<SortChoice> Sorts =
    [
        new("Relevance", ModSortOrder.Relevance),
        new("Most downloads", ModSortOrder.Downloads),
        new("Most liked", ModSortOrder.Endorsements),
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

    /// <summary>The game the search is limited to; none searches every game.</summary>
    public GamePickerViewModel Game { get; }

    // Designer constructor
    public GameBananaSearchViewModel()
    {
        Game = new GamePickerViewModel();
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "999",
            Name = "Sample GameBanana Search Result",
            Author = "TestAuthor",
            BackendId = BackendIds.GameBanana,
            GameDomain = "Sample Game"
        }));
    }

    // DI constructor
    public GameBananaSearchViewModel(GameBananaBackend backend, AppSettings settings)
    {
        _backend = backend;
        Game = new GamePickerViewModel(
            (name, ct) => backend.SearchGamesAsync(name, ct: ct),
            settings.GameBananaGameIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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

    // GameBanana's page each of ours starts at: ours can span several of its pages once the
    // adult filter has thinned them out.
    private readonly List<int> _pageStarts = [1];
    private int _nextSourcePage = 1;

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        CurrentPage = 1;
        _pageStarts.Clear();
        _pageStarts.Add(1);
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasNextPage) return;
        CurrentPage++;
        if (_pageStarts.Count < CurrentPage)
            _pageStarts.Add(_nextSourcePage);
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await LoadPageAsync();
    }

    // Empty search text browses, in the chosen order, one game or all of them.
    private async Task LoadPageAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "GameBanana backend not available";
            return;
        }

        IsLoading = true;

        try
        {
            GameMatch? game = null;
            if (Game.HasText)
            {
                StatusMessage = $"Looking up \"{Game.Text.Trim()}\" on GameBanana...";
                game = await Game.ResolveAsync();
                if (game == null)
                {
                    SearchResults.Clear();
                    TotalResults = 0;
                    HasNextPage = HasPreviousPage = false;
                    StatusMessage = $"No GameBanana game is named \"{Game.Text.Trim()}\". Pick one from the list, or clear it to search every game.";
                    return;
                }
            }

            var terms = SearchText.Trim();
            var where = game != null ? $" in {game.Name}" : "";
            StatusMessage = terms.Length == 0
                ? $"Listing GameBanana mods{where} ({SelectedSort.Label.ToLowerInvariant()})..."
                : $"Searching GameBanana for \"{terms}\"{where}...";

            var result = await _backend.FillPageAsync(new ModSearchQuery
            {
                Terms = terms,
                GameDomain = game?.Id,
                SortBy = SelectedSort.Order,
                PageSize = 50,
                AdultContent = AdultContentOnly
            }, _pageStarts[CurrentPage - 1]);
            _nextSourcePage = result.NextSourcePage;

            SearchResults.Clear();
            foreach (var mod in result.Mods)
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
            HasNextPage = result.HasMore;
            HasPreviousPage = CurrentPage > 1;
            UpdateSelectedCount();
            StatusMessage = result.TotalCount == 0
                ? terms.Length == 0 ? $"No mods listed{where}" : $"No mods found for \"{terms}\""
                : $"Page {CurrentPage}: {SearchResults.Count} mod(s) shown; {result.TotalCount} on GameBanana before the adult filter";
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

    public void UpdateSelectedCount()
    {
        SelectedCount = SearchResults.Count(m => m.IsSelected);
    }

    public IEnumerable<ModDisplayModel> GetSelectedMods()
    {
        return SearchResults.Where(m => m.IsSelected);
    }
}
