using System.Text.Json;
using MindMap.Models;
using MindMap.Services;

namespace MindMap.Tests;

public sealed class MindMapStoreTests
{
    [Fact]
    public void SerializeIncludesDocumentVersion()
    {
        var json = MindMapStore.Serialize(MindMapDocument.CreateStarter());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            MindMapDocument.CurrentVersion,
            document.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void SerializeExcludesComputedDocumentState()
    {
        var json = MindMapStore.Serialize(MindMapDocument.CreateStarter());

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("IsEmpty", out _));
        Assert.False(document.RootElement.TryGetProperty("IsPristineStarter", out _));
    }

    [Fact]
    public void DeserializeRestoresDocumentVersion()
    {
        var json = """
            {
              "Version": 7,
              "Nodes": [
                {
                  "Id": "root",
                  "X": 10,
                  "Y": 20,
                  "Width": 170,
                  "Height": 54,
                  "Text": "Root",
                  "Color": "#4C6EF5",
                  "TextAlignment": "Center"
                }
              ],
              "Connections": []
            }
            """;

        var doc = MindMapStore.Deserialize(json);

        Assert.Equal(7, doc.Version);
        Assert.Single(doc.Nodes);
        Assert.Equal("Root", doc.Nodes[0].Text);
        Assert.Equal("Center", doc.Nodes[0].TextAlignment);
    }

    [Fact]
    public void DeserializeOlderDocumentWithoutVersionUsesCurrentVersion()
    {
        var json = """
            {
              "Nodes": [
                {
                  "Id": "root",
                  "Text": "Older map"
                }
              ],
              "Connections": []
            }
            """;

        var doc = MindMapStore.Deserialize(json);

        Assert.Equal(MindMapDocument.CurrentVersion, doc.Version);
        Assert.Equal("Older map", doc.Nodes.Single().Text);
    }

    [Fact]
    public void SerializeDeserializeRoundtripPreservesDocumentGraph()
    {
        var root = new MindMapNode
        {
            Id = "root",
            X = 100,
            Y = 200,
            Width = 180,
            Height = 60,
            Text = "Root",
            Color = "#4C6EF5",
            TextAlignment = "Right",
        };
        var child = new MindMapNode
        {
            Id = "child",
            X = 360,
            Y = 210,
            Width = 150,
            Height = 46,
            Text = "Child",
            Color = "#FFFFFF",
            TextAlignment = "Left",
        };
        var source = new MindMapDocument
        {
            Version = 3,
            Nodes = { root, child },
            Connections = { new MindMapConnection { FromId = root.Id, ToId = child.Id } },
        };

        var restored = MindMapStore.Deserialize(MindMapStore.Serialize(source));

        Assert.Equal(source.Version, restored.Version);
        Assert.Equal(source.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(source.Connections.Count, restored.Connections.Count);
        Assert.Equal("root", restored.Connections[0].FromId);
        Assert.Equal("child", restored.Connections[0].ToId);
        Assert.Equal(source.Nodes.Select(n => n.Text), restored.Nodes.Select(n => n.Text));
        Assert.Equal(source.Nodes.Select(n => n.Color), restored.Nodes.Select(n => n.Color));
        Assert.Equal(source.Nodes.Select(n => n.TextAlignment), restored.Nodes.Select(n => n.TextAlignment));
    }
}
