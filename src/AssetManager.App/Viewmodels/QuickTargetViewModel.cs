using AssetManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AssetManager.App.ViewModels;

/// <summary>A saved copy destination shown in the sidebar.</summary>
public sealed partial class QuickTargetViewModel : ObservableObject
{
    private readonly ISidebarActions _actions;
    private readonly QuickTarget _model;

    public QuickTargetViewModel(QuickTarget model, ISidebarActions actions)
    {
        _model = model;
        _actions = actions;
        _sortByType = model.SortByType;     // fields, so no change callback fires during setup
        _snakeCaseNames = model.SnakeCaseNames;
    }

    /// <summary>The current state including toggles changed in the context menu.</summary>
    public QuickTarget Model => _model with { SortByType = SortByType, SnakeCaseNames = SnakeCaseNames };

    public string Name => _model.Name;
    public string TargetPath => _model.Path;
    public bool IsGodot => _model.IsGodot;

    public string Detail
    {
        get
        {
            var parts = new List<string> { IsGodot ? "Godot" : "Folder" };
            if (SortByType) parts.Add("by type");
            if (SnakeCaseNames) parts.Add("snake_case");
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private bool _sortByType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private bool _snakeCaseNames;

    partial void OnSortByTypeChanged(bool value) => _ = _actions.UpdateTargetAsync(this);
    partial void OnSnakeCaseNamesChanged(bool value) => _ = _actions.UpdateTargetAsync(this);

    private bool CanCopy() => _actions.HasSelection;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopySelectionAsync() => _actions.CopySelectionToAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _actions.RemoveTargetAsync(this);

    public void RefreshCanCopy() => CopySelectionCommand.NotifyCanExecuteChanged();
}
