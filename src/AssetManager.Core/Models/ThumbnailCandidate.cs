namespace AssetManager.Core.Models;

/// <summary>A hashed texture that still needs a thumbnail (and its dimensions recorded).</summary>
public sealed record ThumbnailCandidate(long Id, string FullPath, string ContentHash);
