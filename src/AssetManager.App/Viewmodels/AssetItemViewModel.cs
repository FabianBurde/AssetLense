using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetManager.Core.Models;
using AssetManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AssetManager.App.ViewModels;

/// <summary>One tile in the grid. The thumbnail loads lazily, so only tiles that scroll into view cost anything.</summary>
public sealed class AssetItemViewModel : ObservableObject
{
    private readonly Asset _asset;
    private readonly AssetLibrary _library;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public AssetItemViewModel(Asset asset, AssetLibrary library)
    {
        _asset = asset;
        _library = library;
    }

    public string FileName => _asset.FileName;
    public string RelativePath => _asset.RelativePath;
    public Asset Asset => _asset;
    public string? FullPath => _library.GetFullPath(_asset);
    public string Extension => _asset.Extension.TrimStart('.').ToUpperInvariant();

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
