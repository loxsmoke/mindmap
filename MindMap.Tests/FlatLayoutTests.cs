using MindMap.Controls;

namespace MindMap.Tests;

public sealed class FlatLayoutTests
{
    [Theory]
    [InlineData(FlatLayout.TopLeft, 1)]
    [InlineData(FlatLayout.TopRight, -1)]
    [InlineData(FlatLayout.BottomLeft, 1)]
    [InlineData(FlatLayout.BottomRight, -1)]
    public void BrokenExampleUsesRootCenteredRingsAfterRebuildAndInsertion(FlatLayout layout, int horizontal)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-level-small-broken.mmap"));
        var doc = MindMap.Services.MindMapStore.Deserialize(json);
        var editor = new MindMapEditor();
        editor.LoadDocument(doc);
        editor.SetFlatLayout(layout);
        var root = doc.Nodes.Single(n => n.Text == "Central Idea");
        var parent = doc.Nodes.Single(n => n.Text == "child1.2");
        var rootPosition = (root.X, root.Y);

        void AssertRings()
        {
            var radii = new Dictionary<int, double>();
            void Visit(MindMap.Models.MindMapNode node, int depth)
            {
                double radius = Math.Sqrt(Math.Pow(node.CenterX - root.CenterX, 2) +
                    Math.Pow(node.CenterY - root.CenterY, 2));
                if (radii.TryGetValue(depth, out double expected)) Assert.Equal(expected, radius, 6);
                else radii[depth] = radius;
                foreach (var connection in doc.Connections.Where(c => c.FromId == node.Id))
                {
                    var child = doc.Nodes.Single(n => n.Id == connection.ToId);
                    Assert.True(horizontal * (child.CenterX - node.CenterX) > 0);
                    Assert.True(horizontal * (child.CenterX - node.CenterX) >=
                        (child.Width + node.Width) / 2 + 90 - 0.000001,
                        $"{child.Text} must leave the centered layout's connector gap after {node.Text}");
                    Visit(child, depth + 1);
                }
            }
            Visit(root, 0);
            Assert.Equal(rootPosition, (root.X, root.Y));
            Assert.Equal(root.CenterY, doc.Nodes.Single(n => n.Text == "child1").CenterY, 6);
            Assert.Equal(root.CenterY, doc.Nodes.Single(n => n.Text == "child1.1").CenterY, 6);
            Assert.Equal(root.CenterY, doc.Nodes.Single(n => n.Text == "child1.2.1").CenterY, 6);
            foreach (string parentText in new[] { "Central Idea", "child1", "child1.2" })
            {
                var siblingParent = doc.Nodes.Single(n => n.Text == parentText);
                var siblings = doc.Connections.Where(c => c.FromId == siblingParent.Id)
                    .Select(c => doc.Nodes.Single(n => n.Id == c.ToId)).ToArray();
                for (int i = 1; i < siblings.Length; i++)
                {
                    int vertical = layout is FlatLayout.BottomLeft or FlatLayout.BottomRight ? -1 : 1;
                    double gap = vertical * (siblings[i].CenterY - siblings[i - 1].CenterY) -
                        (siblings[i].Height + siblings[i - 1].Height) / 2;
                    Assert.InRange(gap, 7.99999, 8.01);
                }
            }
        }

        editor.RebuildLayout();
        AssertRings();
        editor.TestSelectOnly(parent.Id);
        editor.RebuildSelectedLayout();
        AssertRings();
        editor.TestCreateChild(parent.Id);
        AssertRings();
        var positions = doc.Nodes.Select(n => (n.X, n.Y)).ToArray();
        editor.RebuildLayout();
        Assert.Equal(positions, doc.Nodes.Select(n => (n.X, n.Y)).ToArray());
        for (int i = 0; i < doc.Nodes.Count; i++)
        for (int j = i + 1; j < doc.Nodes.Count; j++)
        {
            var a = doc.Nodes[i];
            var b = doc.Nodes[j];
            Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
        }
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft, 1, 1)]
    [InlineData(FlatLayout.TopRight, -1, 1)]
    [InlineData(FlatLayout.BottomLeft, 1, -1)]
    [InlineData(FlatLayout.BottomRight, -1, -1)]
    public void SelectingMatchingCornerPreservesLegacyReference(FlatLayout layout, int horizontal, int vertical)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "one-level-small.mmap"));
        var doc = MindMap.Services.MindMapStore.Deserialize(json);
        Assert.Null(doc.CornerLayout);
        var root = doc.Nodes.Single(n => !doc.Connections.Any(c => c.ToId == n.Id));
        foreach (var node in doc.Nodes.Where(n => n.Id != root.Id))
        {
            node.X = root.CenterX + horizontal * (node.CenterX - root.CenterX) - node.Width / 2;
            node.Y = root.CenterY + vertical * (node.CenterY - root.CenterY) - node.Height / 2;
        }
        var positions = doc.Nodes.Select(n => (n.X, n.Y)).ToArray();
        var editor = new MindMapEditor();
        editor.LoadDocument(doc);
        Assert.Equal(FlatLayout.Centered, editor.CurrentFlatLayout);
        editor.SetFlatLayout(layout);
        Assert.Equal(positions, doc.Nodes.Select(n => (n.X, n.Y)).ToArray());
        Assert.Equal(layout.ToString(), doc.CornerLayout);
        editor.Undo();
        Assert.Null(editor.GetDocument().CornerLayout);
        Assert.Equal(positions, editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray());
    }

    [Fact]
    public void ReselectingSavedCornerPreservesManualPositionsAndCreatesNoUndo()
    {
        var doc = MindMap.Models.MindMapDocument.CreateStarter();
        doc.CornerLayout = "TopLeft";
        var child = new MindMap.Models.MindMapNode { X = 813, Y = 397 };
        doc.Nodes.Add(child);
        doc.Connections.Add(new() { FromId = doc.Nodes[0].Id, ToId = child.Id });
        var editor = new MindMapEditor();
        editor.LoadDocument(doc);
        int changes = 0;
        editor.DocumentChanged += (_, _) => changes++;
        var positions = doc.Nodes.Select(n => (n.X, n.Y)).ToArray();
        editor.SetFlatLayout(FlatLayout.TopLeft);
        Assert.Equal(positions, doc.Nodes.Select(n => (n.X, n.Y)).ToArray());
        Assert.Equal(0, changes);
        editor.Undo();
        Assert.Single(editor.GetDocument().Nodes);
        Assert.Null(editor.GetDocument().CornerLayout);
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void SavedCornerRestoresWithoutRebuildingPositions(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var child = editor.TestCreateChild(root.Id)!;
        editor.SetFlatLayout(layout);
        child.X = 123;
        child.Y = 456;
        var positions = editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray();
        var json = MindMap.Services.MindMapStore.Serialize(editor.GetDocument());

        var reopened = new MindMapEditor();
        bool notified = false;
        reopened.ViewChanged += (_, _) => notified = reopened.CurrentFlatLayout == layout;
        reopened.LoadDocument(MindMap.Services.MindMapStore.Deserialize(json));
        Assert.Equal(layout, reopened.CurrentFlatLayout);
        Assert.True(notified);
        Assert.Equal(positions, reopened.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray());

        reopened.SetFlatLayout(FlatLayout.Centered);
        Assert.Null(reopened.GetDocument().CornerLayout);
        reopened.Undo();
        Assert.Equal(layout, reopened.CurrentFlatLayout);
        Assert.Equal(positions, reopened.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray());
    }

    [Theory]
    [InlineData(FlatLayout.Centered)]
    public void AddingChildrenPreservesParentLevelsWhenSubtreeFits(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var parent = editor.TestCreateChild(root.Id)!;
        var sibling = editor.TestCreateChild(root.Id)!;
        editor.SetFlatLayout(layout);
        int horizontal = layout is FlatLayout.TopRight or FlatLayout.BottomRight ? -1 : 1;
        int vertical = layout is FlatLayout.BottomLeft or FlatLayout.BottomRight ? -1 : 1;
        parent.X = root.X + horizontal * 800;
        parent.Y = root.Y + vertical * 600;
        var anchors = new[] { root, parent, sibling };
        var positions = anchors.Select(n => (n.X, n.Y)).ToArray();

        var first = editor.TestCreateChild(parent.Id)!;
        Assert.Equal(positions, anchors.Select(n => (n.X, n.Y)).ToArray());
        var second = editor.TestCreateChild(parent.Id)!;
        Assert.Equal(positions, anchors.Select(n => (n.X, n.Y)).ToArray());
        Assert.True(first.X + first.Width <= second.X || second.X + second.Width <= first.X ||
                    first.Y + first.Height <= second.Y || second.Y + second.Height <= first.Y);

        // A free grandchild insertion must also leave its parent's siblings alone.
        var existing = editor.GetDocument().Nodes.Select(n => (n.Id, n.X, n.Y)).ToArray();
        var grandchild = editor.TestCreateChild(first.Id)!;
        Assert.Equal(existing, editor.GetDocument().Nodes.Where(n => n.Id != grandchild.Id)
            .Select(n => (n.Id, n.X, n.Y)).ToArray());
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft, 1, 1)]
    [InlineData(FlatLayout.TopRight, -1, 1)]
    [InlineData(FlatLayout.BottomLeft, 1, -1)]
    [InlineData(FlatLayout.BottomRight, -1, -1)]
    public void CrowdedSecondLevelExpandsItsSharedArcWithoutExpandingInnerRing(FlatLayout layout, int horizontal, int vertical)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var branches = Enumerable.Range(0, 2).Select(_ => editor.TestCreateChild(root.Id)!).ToArray();
        foreach (var parent in branches) editor.TestCreateChild(parent.Id);
        editor.SetFlatLayout(layout);
        var firstLevel = branches.Select(n => (n.X, n.Y)).ToArray();
        var doc = editor.GetDocument();
        double Radius(MindMap.Models.MindMapNode node) => Math.Sqrt(
            Math.Pow(node.CenterX - root.CenterX, 2) + Math.Pow(node.CenterY - root.CenterY, 2));
        var existingChild = doc.Nodes.First(n => n.Id != root.Id && !branches.Contains(n));
        double originalRadius = Radius(existingChild);
        foreach (var parent in branches)
        for (int i = 0; i < 8; i++)
        {
            var child = new MindMap.Models.MindMapNode { Width = 150 + i * 10, Height = 46 + i * 3 };
            doc.Nodes.Add(child);
            doc.Connections.Add(new() { FromId = parent.Id, ToId = child.Id });
        }
        editor.RebuildLayout();
        Assert.Equal(firstLevel, branches.Select(n => (n.X, n.Y)).ToArray());
        Assert.True(Radius(existingChild) > originalRadius);
        Assert.All(doc.Nodes.Where(n => n.Id != root.Id && !branches.Contains(n)), n =>
        {
            Assert.Equal(Radius(existingChild), Radius(n), 6);
            Assert.True(horizontal * (n.CenterX - root.CenterX) >= 0);
            Assert.True(vertical * (n.CenterY - root.CenterY) >= 0);
        });
        var nodes = editor.GetDocument().Nodes;
        for (int i = 0; i < nodes.Count; i++)
        for (int j = i + 1; j < nodes.Count; j++)
        {
            var a = nodes[i];
            var b = nodes[j];
            Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                        a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
        }
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft, 1, 1)]
    [InlineData(FlatLayout.TopRight, -1, 1)]
    [InlineData(FlatLayout.BottomLeft, 1, -1)]
    [InlineData(FlatLayout.BottomRight, -1, -1)]
    public void SmallSingleLevelMapUsesSharedQuarterCircle(FlatLayout layout, int horizontal, int vertical)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        // Same dimensions and child count as one-level-small.mmap.
        root.X = 400;
        root.Y = 300;
        root.Width = 170;
        root.Height = 54;
        var children = Enumerable.Range(0, 4).Select(_ => editor.TestCreateChild(root.Id)!).ToList();
        var anchor = (root.X, root.Y);
        editor.SetFlatLayout(layout);

        Assert.Equal(anchor, (root.X, root.Y));
        double? radius = null;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            double dx = horizontal * (child.CenterX - root.CenterX);
            double dy = vertical * (child.CenterY - root.CenterY);
            double distance = Math.Sqrt(dx * dx + dy * dy);
            radius ??= distance;
            Assert.Equal(radius.Value, distance, 6);
            Assert.InRange(dx, 0, 450);
            Assert.InRange(dy, 0, 450);
            if (i > 0)
            {
                double gapX = Math.Abs(child.CenterX - children[i - 1].CenterX) -
                    (child.Width + children[i - 1].Width) / 2;
                double gapY = Math.Abs(child.CenterY - children[i - 1].CenterY) -
                    (child.Height + children[i - 1].Height) / 2;
                Assert.True(Math.Max(gapX, gapY) >= 8 - 0.001);
            }
        }
        var positions = children.Select(n => (n.X, n.Y)).ToArray();
        editor.RebuildLayout();
        Assert.Equal(positions, children.Select(n => (n.X, n.Y)).ToArray());
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void CornerRingsUseCenteredHorizontalClearance(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var child = editor.TestCreateChild(root.Id)!;
        var grandchild = editor.TestCreateChild(child.Id)!;
        editor.SetFlatLayout(layout);

        foreach (var (parent, node) in new[] { (root, child), (child, grandchild) })
        {
            double dx = Math.Abs(node.CenterX - parent.CenterX);
            double dy = Math.Abs(node.CenterY - parent.CenterY);
            Assert.Equal(0, dy, 6);
            double horizontalGap = dx - (node.Width + parent.Width) / 2;
            Assert.Equal(90, horizontalGap, 6);
        }
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void DeepUnevenTreeStaysFiniteAndVisible(FlatLayout layout)
    {
        var editor = new MindMapEditor { Width = 1200, Height = 800 };
        editor.Measure(new Avalonia.Size(1200, 800));
        editor.Arrange(new Avalonia.Rect(0, 0, 1200, 800));
        var document = MindMap.Models.MindMapDocument.CreateStarter();
        var root = document.Nodes.Single();
        var parent = root;
        for (int i = 0; i < 60; i++)
        {
            var leaf = new MindMap.Models.MindMapNode();
            var next = new MindMap.Models.MindMapNode();
            document.Nodes.AddRange(new[] { leaf, next });
            document.Connections.Add(new() { FromId = parent.Id, ToId = leaf.Id });
            document.Connections.Add(new() { FromId = parent.Id, ToId = next.Id });
            parent = next;
        }
        editor.LoadDocument(document);
        editor.SetFlatLayout(layout);

        Assert.InRange(editor.ZoomPercent, 1, 100);
        double scale = editor.ZoomPercent / 100;
        Assert.All(document.Nodes, n =>
        {
            Assert.True(double.IsFinite(n.X) && double.IsFinite(n.Y));
            // Full card-edge clearance needs more room than center-only
            // spacing on this 60-level tree, but must remain bounded.
            Assert.InRange(Math.Abs(n.CenterX - root.CenterX), 0, 50000);
            Assert.InRange(Math.Abs(n.CenterY - root.CenterY), 0, 50000);
            Assert.InRange(n.X * scale + editor.TestPan.X, 0, 1200);
            Assert.InRange((n.X + n.Width) * scale + editor.TestPan.X, 0, 1200);
            Assert.InRange(n.Y * scale + editor.TestPan.Y, 0, 800);
            Assert.InRange((n.Y + n.Height) * scale + editor.TestPan.Y, 0, 800);
        });
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void CornerPackingPreservesBranchOrderWhenEarlierSiblingsOccupyTheTop(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var first = editor.TestCreateChild(root.Id)!;
        var second = editor.TestCreateChild(root.Id)!;
        var a = editor.TestCreateChild(first.Id)!;
        var b = editor.TestCreateChild(first.Id)!;
        var c = editor.TestCreateChild(first.Id)!;
        var single = editor.TestCreateChild(second.Id)!;
        var deep = editor.TestCreateChild(a.Id)!;

        editor.SetFlatLayout(layout);

        double Angle(MindMap.Models.MindMapNode n) => Math.Atan2(Math.Abs(n.CenterY - root.CenterY),
            Math.Abs(n.CenterX - root.CenterX));
        Assert.Equal(0, Angle(first), 6);
        Assert.Equal((first.Height + second.Height) / 2 + 8,
            Math.Abs(second.CenterY - first.CenterY), 6);
        Assert.All(new[] { a, b, c, deep }, n => Assert.InRange(Angle(n), 0, 3 * Math.PI / 8));
        Assert.Equal(Angle(a), Angle(deep), 6);
        // A later branch can use free space above its parent's ray, but
        // cannot displace the earlier siblings on this same depth ring.
        Assert.True(Angle(single) > Angle(c));
        Assert.InRange(Angle(first), Angle(a), Angle(c));
        var positions = editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray();
        editor.RebuildLayout();
        Assert.Equal(positions, editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray());
    }

    [Fact]
    public void DenseSubtreeHasBoundedRingsAndEntireMapFitsViewport()
    {
        var editor = new MindMapEditor { Width = 1000, Height = 700 };
        editor.Measure(new Avalonia.Size(1000, 700));
        editor.Arrange(new Avalonia.Rect(0, 0, 1000, 700));
        var document = MindMap.Models.MindMapDocument.CreateStarter();
        var root = document.Nodes.Single();
        var parents = new List<MindMap.Models.MindMapNode>();
        for (int i = 0; i < 3; i++)
        {
            var parent = new MindMap.Models.MindMapNode();
            document.Nodes.Add(parent);
            parents.Add(parent);
            document.Connections.Add(new() { FromId = root.Id, ToId = parent.Id });
        }
        for (int i = 0; i < 80; i++)
        {
            var child = new MindMap.Models.MindMapNode();
            document.Nodes.Add(child);
            document.Connections.Add(new() { FromId = parents[0].Id, ToId = child.Id });
        }
        editor.LoadDocument(document);
        editor.SetFlatLayout(FlatLayout.TopLeft);
        Assert.All(parents, n => Assert.InRange(Math.Sqrt(Math.Pow(n.CenterX - root.CenterX, 2) +
            Math.Pow(n.CenterY - root.CenterY, 2)), 100, 10000));
        double zoom = editor.ZoomPercent / 100;
        Assert.All(document.Nodes, n =>
        {
            Assert.InRange(n.X * zoom + editor.TestPan.X, 0, 1000);
            Assert.InRange((n.X + n.Width) * zoom + editor.TestPan.X, 0, 1000);
            Assert.InRange(n.Y * zoom + editor.TestPan.Y, 0, 700);
            Assert.InRange((n.Y + n.Height) * zoom + editor.TestPan.Y, 0, 700);
        });
    }

    [Theory]
    [InlineData(FlatLayout.Centered)]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void RebuildSelectedAnchorsParentAndOnlyChangesDescendants(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var parent = editor.TestCreateChild(root.Id)!;
        var child = editor.TestCreateChild(parent.Id)!;
        var grandchild = editor.TestCreateChild(child.Id)!;
        var sibling = editor.TestCreateChild(root.Id)!;
        editor.SetFlatLayout(layout);
        child.X = parent.X;
        child.Y = parent.Y;
        grandchild.X = parent.X;
        grandchild.Y = parent.Y;
        var unchanged = new[] { root, parent, sibling }.Select(n => (n.X, n.Y)).ToArray();
        editor.TestSelectOnly(parent.Id);

        Assert.True(editor.HasSelectedNodes);
        editor.RebuildSelectedLayout();

        Assert.Equal(unchanged, new[] { root, parent, sibling }.Select(n => (n.X, n.Y)).ToArray());
        Assert.NotEqual((parent.X, parent.Y), (child.X, child.Y));
        Assert.NotEqual((parent.X, parent.Y), (grandchild.X, grandchild.Y));
        editor.Undo();
        Assert.Equal((parent.X, parent.Y), (editor.GetDocument().Nodes.Single(n => n.Id == child.Id).X,
            editor.GetDocument().Nodes.Single(n => n.Id == child.Id).Y));
    }

    [Fact]
    public void RebuildSelectedWithoutSelectionDoesNothing()
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var position = (root.X, root.Y);
        Assert.False(editor.HasSelectedNodes);
        Assert.False(editor.CanUndo);
        editor.RebuildSelectedLayout();
        Assert.False(editor.CanUndo);
        Assert.Equal(position, (root.X, root.Y));
    }

    [Fact]
    public void LoadingDocumentResetsViewAndPreservesSavedPositions()
    {
        var editor = new MindMapEditor();
        editor.SetFlatLayout(FlatLayout.BottomRight);
        var document = MindMap.Models.MindMapDocument.CreateStarter();
        document.Nodes[0].X = 321;
        document.Nodes[0].Y = 654;
        bool notified = false;
        editor.ViewChanged += (_, _) =>
            notified = editor.CurrentFlatLayout == FlatLayout.Centered;

        editor.LoadDocument(document);

        Assert.Equal(FlatLayout.Centered, editor.CurrentFlatLayout);
        Assert.True(notified);
        Assert.Equal(321, editor.GetDocument().Nodes[0].X);
        Assert.Equal(654, editor.GetDocument().Nodes[0].Y);
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft)]
    [InlineData(FlatLayout.TopRight)]
    [InlineData(FlatLayout.BottomLeft)]
    [InlineData(FlatLayout.BottomRight)]
    public void UnevenBranchesShareDepthRingsWithoutOverlapping(FlatLayout layout)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var depths = new Dictionary<string, int> { [root.Id] = 0 };
        for (int i = 0; i < 4; i++)
        {
            var parent = editor.TestCreateChild(root.Id)!;
            depths[parent.Id] = 1;
            for (int j = 0; j <= i; j++)
            {
                var child = editor.TestCreateChild(parent.Id)!;
                child.Width = 150 + j * 75;
                child.Height = 46 + j * 20;
                depths[child.Id] = 2;
                if (j == 0)
                    depths[editor.TestCreateChild(child.Id)!.Id] = 3;
            }
        }

        editor.SetFlatLayout(layout);

        var nodes = editor.GetDocument().Nodes;
        var radii = new Dictionary<int, double>();
        foreach (var node in nodes.Where(n => n.Id != root.Id))
        {
            double dx = node.CenterX - root.CenterX;
            double dy = node.CenterY - root.CenterY;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            int depth = depths[node.Id];
            if (radii.TryGetValue(depth, out var expected)) Assert.Equal(expected, radius, 6);
            else radii[depth] = radius;
        }
        for (int i = 0; i < nodes.Count; i++)
        for (int j = i + 1; j < nodes.Count; j++)
        {
            var a = nodes[i];
            var b = nodes[j];
            Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                        a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
        }
    }

    [Theory]
    [InlineData(FlatLayout.TopLeft, false, false)]
    [InlineData(FlatLayout.TopRight, true, false)]
    [InlineData(FlatLayout.BottomLeft, false, true)]
    [InlineData(FlatLayout.BottomRight, true, true)]
    public void SwitchingRebuildsIntoCornerAndRebuildRemainsStable(FlatLayout layout, bool right, bool bottom)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var first = editor.TestCreateChild(root.Id)!;
        editor.TestCreateChild(root.Id);
        editor.TestCreateChild(first.Id);
        first.X = root.X + 3;
        first.Y = root.Y + 7;

        editor.SetFlatLayout(layout);

        Assert.Equal(layout, editor.CurrentFlatLayout);
        void AssertCorner()
        {
            Assert.All(editor.GetDocument().Nodes.Where(n => n.Id != root.Id), node =>
            {
                Assert.True(right ? node.CenterX < root.CenterX : node.CenterX > root.CenterX);
                Assert.True(bottom ? node.CenterY <= root.CenterY : node.CenterY >= root.CenterY);
            });
        }
        AssertCorner();
        var positions = editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray();
        editor.RebuildLayout();
        Assert.Equal(positions, editor.GetDocument().Nodes.Select(n => (n.X, n.Y)).ToArray());
        editor.TestCreateChild(root.Id);
        AssertCorner();

        editor.SetFlatLayout(FlatLayout.Centered);
        Assert.Contains(editor.GetDocument().Nodes, n => n.X + n.Width < root.X);
        Assert.Contains(editor.GetDocument().Nodes, n => n.X > root.X + root.Width);
    }
}
