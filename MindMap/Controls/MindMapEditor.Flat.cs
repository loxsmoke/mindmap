using System;
using System.Collections.Generic;
using System.Linq;
using MindMap.Models;

namespace MindMap.Controls;

public enum FlatLayout { Centered, TopLeft, TopRight, BottomLeft, BottomRight }

public sealed partial class MindMapEditor
{
    public event EventHandler? ViewChanged;

    public FlatLayout CurrentFlatLayout
    {
        get => _doc.CornerLayout switch
        {
            "TopLeft" => FlatLayout.TopLeft,
            "TopRight" => FlatLayout.TopRight,
            "BottomLeft" => FlatLayout.BottomLeft,
            "BottomRight" => FlatLayout.BottomRight,
            _ => FlatLayout.Centered
        };
        private set => _doc.CornerLayout = value == FlatLayout.Centered ? null : value.ToString();
    }

    public void SetFlatLayout(FlatLayout layout)
    {
        CommitEdit();
        if (CurrentFlatLayout == layout)
        {
            ZoomToFit();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        bool adoptingCorner = CurrentFlatLayout == FlatLayout.Centered && layout != FlatLayout.Centered;
        bool restoreCentered = layout == FlatLayout.Centered && CurrentFlatLayout != FlatLayout.Centered;
        PushUndo();
        CurrentFlatLayout = layout;
        if (!_doc.IsEmpty)
        {
            foreach (var root in _doc.Nodes.Where(n => !_doc.Connections.Any(c => c.ToId == n.Id)).ToList())
            {
                // Older files have positions but no layout metadata. Adopting
                // the matching corner should retain an existing radial drawing.
                if (adoptingCorner && IsExistingCornerArrangement(root, layout)) continue;
                // Corner views put every branch on one side. Redistribute them
                // when returning to the centered view.
                if (restoreCentered)
                {
                    var children = ChildrenOf(root).ToList();
                    for (int i = 0; i < children.Count; i++)
                        children[i].X = i < (children.Count + 1) / 2
                            ? root.X + root.Width + ChildHorizontalGap
                            : root.X - ChildHorizontalGap - children[i].Width;
                }
                ReflowTree(root);
            }
        }
        RaiseChanged();
        ZoomToFit();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        Focus();
    }

    private bool IsExistingCornerArrangement(MindMapNode root, FlatLayout layout)
    {
        var branches = ChildrenOf(root).ToList();
        if (branches.Count < 2) return false;
        int horizontal = layout is FlatLayout.TopRight or FlatLayout.BottomRight ? -1 : 1;
        int vertical = layout is FlatLayout.BottomLeft or FlatLayout.BottomRight ? -1 : 1;
        var nodes = new List<MindMapNode> { root };
        var visited = new HashSet<string> { root.Id };
        var levels = new Dictionary<int, List<double>>();
        var queue = new Queue<(MindMapNode Node, int Depth, double ParentRadius)>();
        foreach (var branch in branches) queue.Enqueue((branch, 1, 0));
        while (queue.TryDequeue(out var entry))
        {
            var node = entry.Node;
            if (!visited.Add(node.Id)) return false;
            double x = horizontal * (node.CenterX - root.CenterX);
            double y = vertical * (node.CenterY - root.CenterY);
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) return false;
            double radius = Math.Sqrt(x * x + y * y);
            if (radius <= entry.ParentRadius) return false;
            if (!levels.TryGetValue(entry.Depth, out var radii)) levels[entry.Depth] = radii = new();
            radii.Add(radius);
            nodes.Add(node);
            foreach (var child in ChildrenOf(node)) queue.Enqueue((child, entry.Depth + 1, radius));
        }
        // Allow modest manual adjustments to a ring, but don't mistake an
        // arbitrary one-sided tree for a saved corner layout.
        if (levels.Values.Any(radii => radii.Max() > radii.Min() * 1.3)) return false;
        var angles = branches.Select(n => Math.Atan2(vertical * (n.CenterY - root.CenterY),
            horizontal * (n.CenterX - root.CenterX))).ToList();
        if (angles.Max() - angles.Min() < Math.PI / 18) return false;
        for (int i = 0; i < nodes.Count; i++)
        for (int j = i + 1; j < nodes.Count; j++)
        {
            var a = nodes[i];
            var b = nodes[j];
            if (a.X < b.X + b.Width && b.X < a.X + a.Width &&
                a.Y < b.Y + b.Height && b.Y < a.Y + a.Height) return false;
        }
        return true;
    }

    private void LayoutCorner(MindMapNode root, List<MindMapNode> branches, int? horizontalOverride = null)
    {
        // Corner rings belong to the whole tree, including when an edit or a
        // selected-branch rebuild starts below its root.
        var treeRoot = RootOf(root.Id);
        if (treeRoot != null && treeRoot.Id != root.Id)
        {
            root = treeRoot;
            branches = ChildrenOf(root).ToList();
        }
        int horizontal = horizontalOverride ?? (CurrentFlatLayout is FlatLayout.TopRight or FlatLayout.BottomRight ? -1 : 1);
        int vertical = CurrentFlatLayout is FlatLayout.BottomLeft or FlatLayout.BottomRight ? -1 : 1;
        var entries = new List<(MindMapNode Node, MindMapNode Parent, int Depth)>();
        var visited = new HashSet<string> { root.Id };
        void Visit(MindMapNode node, MindMapNode parent, int depth)
        {
            if (!visited.Add(node.Id)) return;
            entries.Add((node, parent, depth));
            foreach (var child in ChildrenOf(node)) Visit(child, node, depth + 1);
        }
        foreach (var branch in branches) Visit(branch, root, 1);

        const double cardGap = 8;
        double previousRadius = 0;
        var placed = new List<(double X, double Y, double Width, double Height)>
        {
            (0, 0, root.Width, root.Height)
        };
        foreach (var level in entries.GroupBy(e => e.Depth).OrderBy(g => g.Key))
        {
            var ordered = level.ToList();
            var positionsY = new double[ordered.Count];
            double radius = previousRadius + 32;
            for (int i = 0; i < ordered.Count; i++)
            {
                var (node, parent, _) = ordered[i];
                // Every level starts at the corner edge. Reserve only actual
                // card heights, not empty slots for descendants on other rings.
                if (i > 0)
                    positionsY[i] = positionsY[i - 1] +
                        (ordered[i - 1].Node.Height + node.Height) / 2 + cardGap;
                double y = positionsY[i];
                double requiredX = horizontal * (parent.CenterX - root.CenterX) +
                    (parent.Width + node.Width) / 2 + ChildHorizontalGap;
                foreach (var other in placed)
                {
                    if (Math.Abs(y - other.Y) < (node.Height + other.Height) / 2 + cardGap)
                        requiredX = Math.Max(requiredX,
                            other.X + (node.Width + other.Width) / 2 + cardGap);
                }
                // A single root-centered radius must provide the centered
                // layout's horizontal connector clearance for every child.
                radius = Math.Max(radius, Math.Sqrt(requiredX * requiredX + y * y));
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                var node = ordered[i].Node;
                double y = positionsY[i];
                double x = Math.Sqrt(Math.Max(0, radius * radius - y * y));
                node.X = root.CenterX + horizontal * x - node.Width / 2;
                node.Y = root.CenterY + vertical * y - node.Height / 2;
                placed.Add((x, y, node.Width, node.Height));
            }
            previousRadius = radius;
        }
    }

}
