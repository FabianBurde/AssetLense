namespace AssetManager.Core.Models;

// Stored as an integer in SQLite. Only ever append new values; never renumber.
public enum AssetType
{
    Other = 0,
    Texture = 1,
    Mesh = 2,
    Material = 3,
    Audio = 4,
    Animation = 5,
    Shader = 6,
}
