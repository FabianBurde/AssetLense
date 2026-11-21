using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using AssetManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AssetManager.App.ViewModels;

/// <summary>One tile in the grid. The thumbnail loads lazily, so only tiles that scroll into view cost anything.</summary>
public sealed partial class AssetItemViewModel : ObservableObject
{
    private readonly Asset _asset;
    private readonly AssetLibrary _library;
    private readonly Action _onFlagsChanged;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private LicenseResolution? _license;
    private bool _licenseResolved;

    public AssetItemViewModel(Asset asset, AssetLibrary library, Action onFlagsChanged)
    {
        _asset = asset;
        _library = library;
        _onFlagsChanged = onFlagsChanged;
        _isFavorite = asset.IsFavorite; // fields, so no change callbacks fire during setup
        _isOwn = asset.IsOwn;
    }

    public Asset Asset => _asset;
    public string FileName => _asset.FileName;
    public string RelativePath => _asset.RelativePath;
    public string? FullPath => _library.GetFullPath(_asset);
    public string Extension => _asset.Extension.TrimStart('.').ToUpperInvariant();

    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isOwn;

    partial void OnIsOwnChanged(bool value) => InvalidateLicense();

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var value = !IsFavorite;
        await _library.SetFavoriteAsync(new[] { _asset.Id }, value);
        IsFavorite = value;
        _onFlagsChanged();
    }

    // ---- license badge (resolved lazily, so off-screen tiles cost nothing) ----

    private LicenseResolution? License
    {
        get
        {
            if (!_licenseResolved)
            {
                _license = IsOwn ? null : _library.ResolveLicense(_asset);
                _licenseResolved = true;
            }
            return _license;
        }
    }

    public string? LicenseBadgeText => License?.Info.ShortName;
    public string LicenseRiskKey => License?.Info.Risk.ToString() ?? "None";
    public string? LicenseTooltip => License is { } l ? $"{l.Info.Name}: {l.Info.Summary}" : null;

    /// <summary>Call after licenses changed so visible tiles re-read their badge.</summary>
    public void InvalidateLicense()
    {
        _licenseResolved = false;
        OnPropertyChanged(nameof(LicenseBadgeText));
        OnPropertyChanged(nameof(LicenseRiskKey));
        OnPropertyChanged(nameof(LicenseTooltip));
    }

    // ---- details and thumbnail ----

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (_asset.Width > 0 && _asset.Height > 0)
                parts.Add($"{_asset.Width}×{_asset.Height}");
            parts.Add(FormatSize(_asset.SizeBytes));
            return string.Join(" · ", parts);
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Bound by the Image control; the first read starts loading in the background.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                if (CanHaveThumbnail)
                    _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    // Width == 0 is the "could not be decoded" marker set by the thumbnail pass.
    private bool CanHaveThumbnail =>
        _asset.Type == AssetType.Texture && _asset.ContentHash is not null && _asset.Width != 0;

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var asset = _asset;
            var library = _library;
            var image = await Task.Run(() =>
            {
                var path = library.GetThumbnailPath(asset);
                return path is null ? null : LoadBitmap(path);
            });

            if (image is null) return;
            _thumbnail = image;
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(HasThumbnail));
        }
        catch (Exception)
        {
            // A broken or locked thumbnail just leaves the placeholder visible.
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // read fully now so the file isn't kept open
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze(); // frozen images can be created off the UI thread
        return bitmap;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
