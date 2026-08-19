namespace MindMap.Models;

/// <summary>A directed link from one node to another (parent -> child).</summary>
public sealed class MindMapConnection
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
}
