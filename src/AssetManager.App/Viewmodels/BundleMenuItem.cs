using System.Windows.Input;

namespace AssetManager.App.ViewModels;

/// <summary>One entry of the "Add to bundle" submenu.</summary>
public sealed record BundleMenuItem(string Header, ICommand Command);
