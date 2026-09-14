using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using MindMap.Models;

namespace MindMap.Controls;

public sealed partial class MindMapEditor
{
    private bool _threeDView;
    private bool _orbiting;
    private double _yaw = -0.35;
    private double _pitch = 0.5;
    private (double Zoom, double X, double Y) _flatCamera;
    public bool IsThreeDView => _threeDView;
    public event EventHandler? ViewChanged;

    public void SetThreeDView(bool enabled)
    {
        if (_threeDView == enabled) { ViewChanged?.Invoke(this, EventArgs.Empty); return; }
        CommitEdit();
        _mode = DragMode.None;
        _orbiting = false;
        if (enabled) _flatCamera = (_zoom, _panX, _panY);
        _threeDView = enabled;
        if (enabled) FitThreeDView();
        else
        {
            (_zoom, _panX, _panY) = _flatCamera;
            RaiseZoom();
            _layer.InvalidateVisual();
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
        Focus();
    }

    // Breadth-first depths also handle disconnected components and cyclic imports.
    private Dictionary<string, (Point Position, double Depth)> ProjectThreeDNodes()
    {
        var depths = new Dictionary<string, int>();
        var children = _doc.Connections.ToLookup(c => c.FromId, c => c.ToId);
        var incoming = _doc.Connections.Select(c => c.ToId).ToHashSet();
        var ids = _doc.Nodes.Select(n => n.Id).ToHashSet();
        var queue = new Queue<string>();
        void Visit(string id)
        {
            if (!depths.TryAdd(id, 0)) return;
            queue.Enqueue(id);
            while (queue.TryDequeue(out var parent))
                foreach (var child in children[parent])
                    if (ids.Contains(child) && depths.TryAdd(child, depths[parent] + 1))
                        queue.Enqueue(child);
        }
        foreach (var node in _doc.Nodes.Where(n => !incoming.Contains(n.Id))) Visit(node.Id);
        foreach (var node in _doc.Nodes) Visit(node.Id);

        var result = new Dictionary<string, (Point, double)>();
        var bounds = DocumentBounds();
        foreach (var node in _doc.Nodes)
        {
            double x = node.CenterX - bounds.Center.X;
            double y = node.CenterY - bounds.Center.Y;
            double z = depths[node.Id] * 100;
            double rx = x * Math.Cos(_yaw) + z * Math.Sin(_yaw);
            double rz = -x * Math.Sin(_yaw) + z * Math.Cos(_yaw);
            double ry = y * Math.Cos(_pitch) - rz * Math.Sin(_pitch);
            double depth = y * Math.Sin(_pitch) + rz * Math.Cos(_pitch);
            result[node.Id] = (new Point(rx, ry), depth);
        }
        return result;
    }

    private void FitThreeDView()
    {
        var projected = ProjectThreeDNodes();
        if (projected.Count == 0) { _zoom = 1; _panX = Bounds.Width / 2; _panY = Bounds.Height / 2; }
        else
        {
            double left = _doc.Nodes.Min(n => projected[n.Id].Position.X - n.Width / 2);
            double right = _doc.Nodes.Max(n => projected[n.Id].Position.X + n.Width / 2);
            double top = _doc.Nodes.Min(n => projected[n.Id].Position.Y - n.Height / 2);
            double bottom = _doc.Nodes.Max(n => projected[n.Id].Position.Y + n.Height / 2);
            _zoom = Math.Clamp(Math.Min(Math.Max(1, Bounds.Width - 100) / (right - left + 20),
                Math.Max(1, Bounds.Height - 100) / (bottom - top + 20)), 0.01, 1);
            _panX = Bounds.Width / 2 - (left + right) / 2 * _zoom;
            _panY = Bounds.Height / 2 - (top + bottom) / 2 * _zoom;
        }
        RaiseZoom();
        _layer.InvalidateVisual();
    }

    private void DrawThreeDView(DrawingContext ctx)
    {
        var projected = ProjectThreeDNodes();
        var colors = BuildBranchColors();
        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panX, _panY)))
        {
            foreach (var edge in _doc.Connections)
            {
                if (!projected.TryGetValue(edge.FromId, out var from) || !projected.TryGetValue(edge.ToId, out var to)) continue;
                var color = colors.TryGetValue(edge.ToId, out var branch) ? branch : ConnectionFallbackColor;
                ctx.DrawLine(new Pen(new SolidColorBrush(color), 2.5), from.Position, to.Position);
            }
            // Orthographic 3D projection with camera-facing labels, painted back to front.
            foreach (var node in _doc.Nodes.OrderBy(n => projected[n.Id].Depth))
            {
                var center = projected[node.Id].Position;
                var rect = new Rect(center.X - node.Width / 2, center.Y - node.Height / 2, node.Width, node.Height);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), null,
                    new Rect(rect.X + 6, rect.Y + 8, rect.Width, rect.Height), 10, 10);
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse(node.Color)), new Pen(new SolidColorBrush(LightNodeBorderColor), 1), rect, 10, 10);
                var text = MakeText(node);
                ctx.DrawText(text, new Point(rect.X + 10, rect.Y + (rect.Height - text.Height) / 2));
            }
        }
        var hint = new FormattedText("3D View • Drag: rotate • Right-drag: pan • Wheel: zoom • Choose Flat to edit",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI, sans-serif"),
            11, new SolidColorBrush(HudTextColor));
        ctx.DrawText(hint, new Point(12, Bounds.Height - 22));
    }
}
