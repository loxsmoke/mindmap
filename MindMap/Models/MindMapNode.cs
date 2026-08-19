using System;

namespace MindMap.Models;

/// <summary>A single node (topic) on the map. Coordinates are in world space.</summary>
public sealed class MindMapNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 150;
    public double Height { get; set; } = 46;
    public string Text { get; set; } = "";
    /// <summary>Fill colour as a hex string, e.g. "#4C6EF5".</summary>
    public string Color { get; set; } = "#4C6EF5";
    /// <summary>Text alignment: Left, Center, or Right.</summary>
    public string TextAlignment { get; set; } = "Left";

    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
