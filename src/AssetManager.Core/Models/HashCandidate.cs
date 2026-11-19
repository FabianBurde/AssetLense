namespace AssetManager.Core.Models;

/// <summary>A file that still needs its content hash computed.</summary>
public sealed record HashCandidate(long Id, string FullPath, long SizeBytes);
