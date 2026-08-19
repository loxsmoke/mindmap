using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MindMap.Models;

/// <summary>The whole document: everything needed to persist a map.</summary>
public sealed class MindMapDocument
{
    public const int CurrentVersion = 1;
    public const string DefaultRootText = "Central Idea";

    public int Version { get; set; } = CurrentVersion;
    public List<MindMapNode> Nodes { get; set; } = new();
    public List<MindMapConnection> Connections { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Nodes.Count == 0;

    [JsonIgnore]
    public bool IsPristineStarter =>
        IsEmpty ||
        (Nodes.Count == 1 && Connections.Count == 0 && Nodes[0].Text == DefaultRootText);

    public static MindMapDocument CreateStarter()
    {
        var root = new MindMapNode
        {
            X = 400,
            Y = 300,
            Width = 170,
            Height = 54,
            Text = DefaultRootText,
            Color = "#4C6EF5",
        };
        return new MindMapDocument { Nodes = { root } };
    }

    /// <summary>A deep copy — used for undo snapshots.</summary>
    public MindMapDocument Clone()
    {
        var copy = new MindMapDocument { Version = Version };
        foreach (var n in Nodes)
            copy.Nodes.Add(new MindMapNode
            {
                Id = n.Id, X = n.X, Y = n.Y, Width = n.Width, Height = n.Height,
                Text = n.Text, Color = n.Color, TextAlignment = n.TextAlignment,
            });
        foreach (var c in Connections)
            copy.Connections.Add(new MindMapConnection { FromId = c.FromId, ToId = c.ToId });
        return copy;
    }
}
