using MindMap.Models;
using MindMap.Services;

namespace MindMap.Tests;

public sealed class MindMapHtmlExporterTests
{
    [Fact]
    public void ExportCreatesStandaloneHtmlWithSvgNodesAndConnections()
    {
        var root = new MindMapNode
        {
            Id = "root",
            Text = "Root",
            X = 10,
            Y = 20,
            Width = 160,
            Height = 54,
            Color = "#4C6EF5",
        };
        var child = new MindMapNode
        {
            Id = "child",
            Text = "Child",
            X = 260,
            Y = 90,
            Width = 150,
            Height = 46,
            Color = "#FFFFFF",
            TextAlignment = "Right",
        };
        var doc = new MindMapDocument
        {
            Nodes = { root, child },
            Connections = { new MindMapConnection { FromId = root.Id, ToId = child.Id } },
        };

        var html = MindMapHtmlExporter.Export(doc, "Example Map");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", html);
        Assert.Contains("<path d=\"M 170 47 C 215 47, 215 113, 260 113\"", html);
        Assert.Contains("id=\"node-child\"", html);
        Assert.Contains("text-align: right; justify-content: flex-end;", html);
        Assert.Contains(">Child</div>", html);
    }

    [Fact]
    public void ExportHtmlEncodesTitleTextAndIds()
    {
        var doc = new MindMapDocument
        {
            Nodes =
            {
                new MindMapNode
                {
                    Id = "node\"one",
                    Text = "<script>alert(\"x\")</script>",
                    X = 0,
                    Y = 0,
                    Width = 150,
                    Height = 46,
                },
            },
        };

        var html = MindMapHtmlExporter.Export(doc, "A <B>");

        Assert.Contains("<title>A &lt;B&gt;</title>", html);
        Assert.Contains("aria-label=\"A &lt;B&gt;\"", html);
        Assert.Contains("id=\"node-node&quot;one\"", html);
        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }
}
