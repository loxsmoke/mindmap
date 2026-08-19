using System.Text.Json;
using MindMap.Models;

namespace MindMap.Services;

/// <summary>JSON persistence for a <see cref="MindMapDocument"/>.</summary>
public static class MindMapStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(MindMapDocument doc) =>
        JsonSerializer.Serialize(doc, Options);

    public static MindMapDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<MindMapDocument>(json, Options) ?? new MindMapDocument();
}
