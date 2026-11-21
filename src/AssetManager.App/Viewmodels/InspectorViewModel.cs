using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using AssetManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AssetManager.App.ViewModels;

public enum InspectorChange { Flags, Tags, License }

public sealed record ConditionViewModel(string Glyph, string Text, string Kind);

public sealed record LicenseChoice(string? Id, string Label);

public sealed partial class TagChipViewModel : ObservableObject
{
    private readonly Func<TagChipViewModel, Task> _remove;

    public TagChipViewModel(string name, int count, int total, Func<TagChipViewModel, Task> remove)
    {
        Name = name;
        Label = count < total ? $"{name} ({count}/{total})" : name;
        _remove = remove;
    }

    public string Name { get; }
    public string Label { get; }

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);
}

/// <summary>The right-hand panel: favorite/mine toggles, tags, license conditions and readme of the selection.</summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly AssetLibrary _library;
    private readonly Action<string> _status;
    private readonly Func<InspectorChange, Task> _changed;
    private IReadOnlyList<AssetItemViewModel> _selection = Array.Empty<AssetItemViewModel>();
    private CancellationTokenSource? _loadCts;
    private (long RootId, string Folder)? _scope;
    private bool _suppressChoice;

    public InspectorViewModel(AssetLibrary library, Action<string> status, Func<InspectorChange, Task> changed)
    {
        _library = library;
        _status = status;
        _changed = changed;

        var choices = new List<LicenseChoice> { new(null, "Automatic (from files)") };
        choices.AddRange(LicenseCatalog.CommonIds.Select(id => new LicenseChoice(id, LicenseCatalog.Get(id).Name)));
        LicenseChoices = choices;
        _selectedLicenseChoice = choices[0];
    }

    public IReadOnlyList<LicenseChoice> LicenseChoices { get; }

    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isSingle;
    [ObservableProperty] private AssetItemViewModel? _primary;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _favoriteText = "☆ Favorite";
    [ObservableProperty] private string _ownText = "Mark as mine";

    [ObservableProperty] private IReadOnlyList<TagChipViewModel> _tags = Array.Empty<TagChipViewModel>();
    [ObservableProperty] private string _newTagText = "";
    [ObservableProperty] private IReadOnlyList<string> _knownTagNames = Array.Empty<string>();

    [ObservableProperty] private bool _showLicense;
    [ObservableProperty] private string _licenseTitle = "";
    [ObservableProperty] private string _licenseSource = "";
    [ObservableProperty] private string _licenseSummary = "";
    [ObservableProperty] private string _licenseRiskKey = "Unknown";
    [ObservableProperty] private IReadOnlyList<ConditionViewModel> _conditions = Array.Empty<ConditionViewModel>();
    [ObservableProperty] private string? _licenseText;
    [ObservableProperty] private LicenseChoice _selectedLicenseChoice;
    [ObservableProperty] private string _scopeText = "";

    [ObservableProperty] private string? _readmeText;
    [ObservableProperty] private string _readmeSource = "";

    private List<long> Ids() => _selection.Select(a => a.Asset.Id).ToList();

    // ------------------------------------------------------------ selection

    public async Task SetSelectionAsync(IReadOnlyList<AssetItemViewModel> selection)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        _selection = selection;
        HasSelection = selection.Count > 0;
        IsSingle = selection.Count == 1;
        Primary = IsSingle ? selection[0] : null;

        if (!HasSelection)
        {
            Tags = Array.Empty<TagChipViewModel>();
            ShowLicense = false;
            ReadmeText = null;
            ReadmeSource = "";
            return;
        }

        Title = IsSingle ? selection[0].FileName : $"{selection.Count:N0} assets selected";
        Subtitle = IsSingle ? $"{selection[0].Details}\n{selection[0].RelativePath}" : "";
        RefreshFlagTexts();

        try
        {
            await Task.Delay(80, cts.Token); // skip work while the user is still shift-clicking through tiles
            await LoadTagsAsync(cts.Token);

            if (IsSingle)
            {
                await LoadLicenseAsync(selection[0], cts.Token);
            }
            else
            {
                ShowLicense = false;
                ReadmeText = null;
                ReadmeSource = "";
            }
        }
        catch (OperationCanceledException)
        {
            // a newer selection replaced this one
        }
        catch (Exception ex)
        {
            _status($"Could not load details: {ex.Message}");
        }
    }

    public void RefreshFlagTexts()
    {
        if (_selection.Count == 0) return;
        FavoriteText = _selection.All(a => a.IsFavorite) ? "★ Favorited" : "☆ Favorite";
        OwnText = _selection.All(a => a.IsOwn) ? "✓ My creation" : "Mark as mine";
    }

    // ------------------------------------------------------------ favorite / own

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (!HasSelection) return;
        var value = !_selection.All(a => a.IsFavorite);
        await _library.SetFavoriteAsync(Ids(), value);
        foreach (var a in _selection) a.IsFavorite = value;
        RefreshFlagTexts();
        await _changed(InspectorChange.Flags);
    }

    [RelayCommand]
    private async Task ToggleOwnAsync()
    {
        if (!HasSelection) return;
        var value = !_selection.All(a => a.IsOwn);
        await _library.SetOwnAsync(Ids(), value);
        foreach (var a in _selection) a.IsOwn = value;
        RefreshFlagTexts();
        await _changed(InspectorChange.Flags);
        if (IsSingle) await LoadLicenseAsync(_selection[0], CancellationToken.None);
    }

    // ------------------------------------------------------------ tags

    private async Task LoadTagsAsync(CancellationToken ct)
    {
        var total = _selection.Count;
        var counts = await _library.GetTagsForAssetsAsync(Ids(), ct);
        ct.ThrowIfCancellationRequested();
        Tags = counts.Select(c => new TagChipViewModel(c.Name, c.Count, total, RemoveTagAsync)).ToList();
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var names = TagNames.Split(NewTagText);
        if (names.Count == 0 || !HasSelection) return;

        try
        {
            await _library.AddTagsAsync(Ids(), names);
            NewTagText = "";
            await LoadTagsAsync(CancellationToken.None);
            await _changed(InspectorChange.Tags);
        }
        catch (Exception ex)
        {
            _status($"Could not add tag: {ex.Message}");
        }
    }

    private async Task RemoveTagAsync(TagChipViewModel chip)
    {
        try
        {
            await _library.RemoveTagAsync(Ids(), chip.Name);
            await LoadTagsAsync(CancellationToken.None);
            await _changed(InspectorChange.Tags);
        }
        catch (Exception ex)
        {
            _status($"Could not remove tag: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ license and readme

    private async Task LoadLicenseAsync(AssetItemViewModel item, CancellationToken ct)
    {
        var asset = item.Asset;
        var resolution = _library.ResolveLicense(asset);
        _scope = (asset.RootId, resolution?.SourceFolder ?? asset.Folder);
        ShowLicense = true;

        if (item.IsOwn)
        {
            LicenseTitle = "Your own work";
            LicenseSummary = "No third-party license applies to your own creations.";
            LicenseRiskKey = "Free";
            LicenseSource = "";
            Conditions = Array.Empty<ConditionViewModel>();
            LicenseText = null;
        }
        else if (resolution is null)
        {
            LicenseTitle = "No license found";
            LicenseSummary = "There is no license file in this folder or above. Check where the asset came from before using it.";
            LicenseRiskKey = "Unknown";
            LicenseSource = "";
            Conditions = Array.Empty<ConditionViewModel>();
            LicenseText = null;
        }
        else
        {
            var info = resolution.Info;
            LicenseTitle = info.Name;
            LicenseSummary = info.Summary;
            LicenseRiskKey = info.Risk.ToString();
            Conditions = BuildConditions(info);
            LicenseSource = resolution switch
            {
                { IsOverride: true } => $"Set manually for {FolderLabel(resolution.SourceFolder)}",
                { FromReadme: true } => $"Detected in the README ({resolution.SourceFile}), less certain",
                _ => $"From {resolution.SourceFile}",
            };

            var docs = await _library.GetFolderDocsAsync(asset.RootId, resolution.SourceFolder, ct);
            ct.ThrowIfCancellationRequested();
            LicenseText = docs?.LicenseFromReadme == true ? null : docs?.LicenseText;
        }

        _suppressChoice = true;
        SelectedLicenseChoice = LicenseChoices.FirstOrDefault(c => resolution is { IsOverride: true } && c.Id == resolution.Info.Id)
                                ?? LicenseChoices[0];
        _suppressChoice = false;
        ScopeText = $"A change applies to {FolderLabel(_scope.Value.Folder)} and everything below it.";

        var readme = _library.ResolveReadme(asset);
        if (readme is null)
        {
            ReadmeText = null;
            ReadmeSource = "";
        }
        else
        {
            var docs = await _library.GetFolderDocsAsync(readme.RootId, readme.Folder, ct);
            ct.ThrowIfCancellationRequested();
            ReadmeText = docs?.ReadmeText;
            ReadmeSource = readme.File;
        }
    }

    partial void OnSelectedLicenseChoiceChanged(LicenseChoice value)
    {
        if (_suppressChoice || _scope is null) return;
        _ = ApplyOverrideAsync(value.Id);
    }

    private async Task ApplyOverrideAsync(string? licenseId)
    {
        try
        {
            var (rootId, folder) = _scope!.Value;
            await _library.SetLicenseOverrideAsync(rootId, folder, licenseId);
            await _changed(InspectorChange.License);
            if (IsSingle) await LoadLicenseAsync(_selection[0], CancellationToken.None);
        }
        catch (Exception ex)
        {
            _status($"Could not change the license: {ex.Message}");
        }
    }

    private static string FolderLabel(string folder) => folder.Length == 0 ? "the library root" : $"“{folder}”";

    private static IReadOnlyList<ConditionViewModel> BuildConditions(LicenseInfo info)
    {
        var list = new List<ConditionViewModel>
        {
            info.CommercialUse switch
            {
                Tri.Yes => new ConditionViewModel("✓", "Commercial use allowed", "ok"),
                Tri.No => new ConditionViewModel("✗", "No commercial use", "bad"),
                _ => new ConditionViewModel("?", "Commercial use: unclear", "unknown"),
            },
            info.AttributionRequired switch
            {
                Tri.Yes => new ConditionViewModel("⚠", "Attribution required", "warn"),
                Tri.No => new ConditionViewModel("✓", "No attribution needed", "ok"),
                _ => new ConditionViewModel("?", "Attribution: unclear", "unknown"),
            },
            info.Modification switch
            {
                Tri.Yes => new ConditionViewModel("✓", "Modifications allowed", "ok"),
                Tri.No => new ConditionViewModel("✗", "No modifications (no derivatives)", "bad"),
                _ => new ConditionViewModel("?", "Modifications: unclear", "unknown"),
            },
        };

        if (info.ShareAlike == Tri.Yes)
            list.Add(new ConditionViewModel("⚠", "Share-alike: derivatives must use the same license", "warn"));
        else if (info.ShareAlike == Tri.Unknown)
            list.Add(new ConditionViewModel("?", "Share-alike: unclear", "unknown"));

        return list;
    }
}
