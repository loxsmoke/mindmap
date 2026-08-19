using MindMap.Controls;
using MindMap.Services;

namespace MindMap.Tests;

public sealed class ClipboardOutlineTests
{
    [Fact]
    public void ExportedOutlineCanBeImportedAgain()
    {
        var source = new MindMapEditor();
        Assert.True(source.ImportOutline("""
            - Root
              - Alpha
                - Alpha child
              - Beta
            """));

        var clipboardText = source.ExportOutlineForClipboard();
        var target = new MindMapEditor();

        Assert.True(target.ImportOutline(clipboardText));
        Assert.Equal(
            source.GetDocument().Nodes.Select(n => n.Text).Order(),
            target.GetDocument().Nodes.Select(n => n.Text).Order());
        Assert.Equal(source.GetDocument().Connections.Count, target.GetDocument().Connections.Count);
    }

    [Fact]
    public void ImportOutlineAppendsWhenMapAlreadyExists()
    {
        var editor = new MindMapEditor();
        Assert.True(editor.ImportOutline("""
            - Existing root
              - Existing child
            """));
        var existingMaxY = editor.GetDocument().Nodes.Max(n => n.Y + n.Height);

        Assert.True(editor.ImportOutline("""
            - Imported root
              - Imported child
            """));

        var doc = editor.GetDocument();
        Assert.Equal(
            new[] { "Existing child", "Existing root", "Imported child", "Imported root" },
            doc.Nodes.Select(n => n.Text).Order());
        Assert.Equal(2, doc.Connections.Count);

        var importedRoot = doc.Nodes.Single(n => n.Text == "Imported root");
        Assert.True(importedRoot.Y >= existingMaxY + 100 - 0.001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t\r\n")]
    public void EmptyClipboardTextDoesNotParseAsOutline(string text)
    {
        Assert.Null(OutlineImporter.Parse(text));
    }

    [Theory]
    [InlineData("{\"Nodes\":[{\"Text\":\"saved map\"}]}")]
    [InlineData("https://example.com/not-an-outline")]
    [InlineData("plain text copied from another app")]
    public void NonOutlineClipboardTextDoesNotThrow(string text)
    {
        var exception = Record.Exception(() => OutlineImporter.Parse(text));

        Assert.Null(exception);
    }
}
