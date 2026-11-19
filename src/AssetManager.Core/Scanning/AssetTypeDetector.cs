using AssetManager.Core.Models;

namespace AssetManager.Core.Scanning;

public static class AssetTypeDetector
{
    private static readonly Dictionary<string, AssetType> Map = Build();

    /// <param name="extension">Including the dot, e.g. ".png". Case-insensitive.</param>
    public static AssetType Detect(string extension) =>
        Map.TryGetValue(extension, out var type) ? type : AssetType.Other;

    private static Dictionary<string, AssetType> Build()
    {
        var map = new Dictionary<string, AssetType>(StringComparer.OrdinalIgnoreCase);

        void Add(AssetType type, params string[] extensions)
        {
            foreach (var e in extensions) map[e] = type;
        }

        Add(AssetType.Texture, ".png", ".jpg", ".jpeg", ".tga", ".dds", ".bmp", ".tif", ".tiff",
            ".psd", ".exr", ".hdr", ".webp", ".ktx", ".ktx2");
        Add(AssetType.Mesh, ".fbx", ".obj", ".gltf", ".glb", ".blend", ".dae", ".3ds", ".ply", ".stl",
            ".usd", ".usda", ".usdc");
        Add(AssetType.Material, ".mat", ".mtl", ".sbsar");
        Add(AssetType.Audio, ".wav", ".ogg", ".mp3", ".flac");
        Add(AssetType.Animation, ".anim", ".bvh");
        Add(AssetType.Shader, ".hlsl", ".glsl", ".shader", ".fx");

        return map;
    }
}
