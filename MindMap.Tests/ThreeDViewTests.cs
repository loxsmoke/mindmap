using Avalonia;
using MindMap.Controls;
using MindMap.Models;

namespace MindMap.Tests;

public sealed class ThreeDViewTests
{
    [Fact]
    public void SwitchingViewsPreservesDocumentAndRestoresFlatCamera()
    {
        var editor = new MindMapEditor();
        editor.Arrange(new Rect(0, 0, 1000, 700));
        var root = editor.GetDocument().Nodes.Single();
        editor.TestCreateChild(root.Id);
        editor.ZoomToFit();
        var positions = editor.GetDocument().Nodes.Select(n => (n.Id, n.X, n.Y)).ToArray();
        var zoom = editor.ZoomPercent;
        var pan = editor.TestPan;
        var changes = 0;
        editor.DocumentChanged += (_, _) => changes++;

        Assert.False(editor.IsThreeDView);
        editor.SetThreeDView(true);
        Assert.True(editor.IsThreeDView);
        editor.ZoomIn();
        editor.SetThreeDView(false);

        Assert.False(editor.IsThreeDView);
        Assert.Equal(zoom, editor.ZoomPercent);
        Assert.Equal(pan, editor.TestPan);
        Assert.Equal(positions, editor.GetDocument().Nodes.Select(n => (n.Id, n.X, n.Y)).ToArray());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ThreeDFitHandlesCyclesAndDisconnectedNodes()
    {
        var editor = new MindMapEditor();
        editor.Arrange(new Rect(0, 0, 1000, 700));
        var a = new MindMapNode { X = -300, Y = -100 };
        var b = new MindMapNode { X = 400, Y = 200 };
        var isolated = new MindMapNode { X = 10000, Y = -10000 };
        editor.LoadDocument(new MindMapDocument
        {
            Nodes = { a, b, isolated },
            Connections =
            {
                new MindMapConnection { FromId = a.Id, ToId = b.Id },
                new MindMapConnection { FromId = b.Id, ToId = a.Id }
            }
        });
        editor.SetThreeDView(true);
        editor.ZoomToFit();
        Assert.True(double.IsFinite(editor.ZoomPercent));
        Assert.True(double.IsFinite(editor.TestPan.X));
        Assert.True(double.IsFinite(editor.TestPan.Y));
    }
}
