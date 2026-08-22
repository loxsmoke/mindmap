using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MindMap.Models;

namespace MindMap.Controls;

/// <summary>
/// A self-contained, pannable/zoomable mind-map canvas. Nodes and connections are
/// drawn directly in <see cref="Render"/>; a single overlay <see cref="TextBox"/> is
/// reused for in-place text editing.
///
/// Interaction summary:
///   • Double-click empty space  -> new node
///   • Double-click a node / F2  -> edit its text
///   • Drag a node               -> move (moves the whole selection)
///   • Drag the handle on a node's right edge -> connect to another node,
///                                               or drop on empty space to spawn a child
///   • Tab                       -> add a child of the selected node
///   • Shift+Tab                 -> add a child on the root's left side
///   • Enter                     -> add a sibling of the selected node
///   • Delete / Backspace        -> delete selection
///   • Ctrl+click                -> toggle selection; drag on empty space -> marquee select
///   • Right/Middle/Space+drag   -> pan;  wheel -> pan (Shift: horizontal, Ctrl: zoom)
/// </summary>
public sealed class MindMapEditor : Canvas
{
    private enum DragMode { None, Panning, MovingNodes, Marquee, Connecting }

    private MindMapDocument _doc = MindMapDocument.CreateStarter();
    private readonly HashSet<string> _selected = new();

    private double _zoom = 1.0;
    private double _panX = 0;
    private double _panY = 0;

    private DragMode _mode = DragMode.None;
    private Point _pressScreen;
    private Point _pressWorld;
    private bool _spaceDown;
    private string? _hoverNodeId;
    private string? _dropParentCandidateId;

    // Moving
    private readonly Dictionary<string, Point> _moveOrigin = new();
    private MindMapDocument? _pendingMoveSnapshot;
    private string? _dragPrimaryNodeId;
    // Marquee
    private Rect _marquee;
    // Connecting
    private string? _connectFromId;
    private Point _connectCurrentWorld;

    // In-place editing
    private readonly TextBox _editor;
    private string? _editingNodeId;
    private bool _editingIsNew;      // node was created for this edit session
    private bool _transitioning;     // mid Enter/Tab hand-off; ignore stray LostFocus

    // Panel.Render is sealed, so custom drawing lives on a hit-transparent child layer.
    private readonly DrawLayer _layer;

    // Undo: snapshots of the document taken just before each mutating action.
    private readonly List<MindMapDocument> _undo = new();
    private const int UndoLimit = 100;
    private const double ChildHorizontalGap = 90;
    private const double ChildVerticalGap = 24;
    private const double ReparentOverlapThreshold = 0.20;
    #region Colors
    private static Color CanvasBackgroundColor { get; } = Color.Parse("#F5F6F8");
    private static Color EditorBorderColor { get; } = Color.Parse("#1C1E21");
    private static Color GridDotColor { get; } = Color.Parse("#D7DBE0");
    private static Color ConnectionFallbackColor { get; } = Color.Parse("#98A2B3");
    private static Color LightNodeBorderColor { get; } = Color.Parse("#CED4DA");
    private static Color HudTextColor { get; } = Color.Parse("#8A94A6");
    internal static Color PrimaryBranchColor { get; } = Color.Parse("#4C6EF5");
    internal static Color DangerBranchColor { get; } = Color.Parse("#F03E3E");
    private static Color WarningBranchColor { get; } = Color.Parse("#F59F00");
    private static Color SuccessBranchColor { get; } = Color.Parse("#37B24D");
    private static Color PurpleBranchColor { get; } = Color.Parse("#7048E8");
    private static Color TealBranchColor { get; } = Color.Parse("#1098AD");
    private static Color PinkBranchColor { get; } = Color.Parse("#E64980");
    private static Color OrangeBranchColor { get; } = Color.Parse("#F76707");
    #endregion
    private TextAlignment _currentTextAlignment = TextAlignment.Left;

    public event EventHandler? DocumentChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? CopyRequested;
    public event EventHandler? PasteRequested;

    public double ZoomPercent => _zoom * 100.0;
    public TextAlignment CurrentTextAlignment => _currentTextAlignment;

    internal Point TestPan => new(_panX, _panY);

    internal IReadOnlyDictionary<string, Color> TestBranchLineColors() => BuildBranchColors();

    internal void TestSelectOnly(string id)
    {
        SelectOnly(id);
        UpdateCurrentTextAlignmentFromSelection();
        RaiseSelectionChanged();
    }

    internal void TestBeginEditNode(string id)
    {
        var node = NodeById(id);
        if (node != null) BeginEdit(node);
    }

    internal (int Start, int End) TestEditorSelection => (_editor.SelectionStart, _editor.SelectionEnd);

    internal MindMapNode? TestCreateChild(string parentId, bool leftOfRoot = false)
    {
        var parent = NodeById(parentId);
        if (parent == null) return null;

        PushUndo();
        var child = CreateChild(parent, leftOfRoot: leftOfRoot);
        SelectOnly(child.Id);
        RaiseSelectionChanged();
        EnsureNodeVisible(child);
        RaiseChanged();
        return child;
    }

    internal bool TestReparentNode(string nodeId, string newParentId)
    {
        var changed = ReparentNode(nodeId, newParentId);
        if (changed)
        {
            SelectOnly(nodeId);
            RaiseSelectionChanged();
            RaiseChanged();
        }
        return changed;
    }

    internal bool TestMoveNodeAndResolveDrag(string nodeId, double x, double y)
    {
        var node = NodeById(nodeId);
        if (node == null) return false;

        SelectOnly(nodeId);
        _dragPrimaryNodeId = nodeId;
        _moveOrigin.Clear();
        foreach (var n in NodesToMoveForDrag(nodeId)) _moveOrigin[n.Id] = new Point(n.X, n.Y);
        node.X = x;
        node.Y = y;
        var changed = TryReparentMovedNode(new Point(node.CenterX, node.CenterY)) || TryReflowMovedBranch();
        _moveOrigin.Clear();
        _dragPrimaryNodeId = null;
        _dropParentCandidateId = null;
        if (changed) RaiseChanged();
        return changed;
    }

    internal void TestEnsureNodeVisible(string nodeId)
    {
        var node = NodeById(nodeId);
        if (node != null) EnsureNodeVisible(node);
    }

    public MindMapEditor()
    {
        Focusable = true;
        Background = new SolidColorBrush(CanvasBackgroundColor);
        ClipToBounds = true;

        _layer = new DrawLayer(this) { IsHitTestVisible = false };
        SetLeft(_layer, 0);
        SetTop(_layer, 0);
        Children.Add(_layer);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Padding = new Thickness(6, 4),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(EditorBorderColor),
            Background = Brushes.White,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _editor.LostFocus += (_, _) => { if (!_transitioning) CommitEdit(); };
        // Tunnel: intercept Enter/Tab BEFORE the TextBox consumes them (AcceptsReturn=true
        // otherwise eats Enter as a newline). Shift+Enter is left alone to become a newline.
        _editor.AddHandler(KeyDownEvent, EditorKeyDown, RoutingStrategies.Tunnel);
        Children.Add(_editor);
    }

    // ---------------------------------------------------------------- Public API

    public MindMapDocument GetDocument() => _doc;

    /// <summary>Records the current document so the next mutation can be reverted with Undo.</summary>
    private void PushUndo()
    {
        _undo.Add(_doc.Clone());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
    }

    /// <summary>Reverts the most recent change.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        CancelEdit();
        _doc = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _selected.Clear();
        _hoverNodeId = null;
        UpdateCurrentTextAlignmentFromSelection();
        _layer.InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        RaiseSelectionChanged();
    }

    public bool CanUndo => _undo.Count > 0;

    public bool SelectAllForCurrentContext()
    {
        if (_editingNodeId != null && _editor.IsVisible)
        {
            _editor.Focus();
            _editor.SelectAll();
            return false;
        }

        SelectAllNodes();
        return true;
    }

    public void SelectAllNodes()
    {
        CommitEdit();
        _selected.Clear();
        foreach (var n in _doc.Nodes) _selected.Add(n.Id);
        UpdateCurrentTextAlignmentFromSelection();
        RaiseSelectionChanged();
        _layer.InvalidateVisual();
    }

    public void LoadDocument(MindMapDocument doc)
    {
        CancelEdit();
        PushUndo();
        _doc = doc;
        _selected.Clear();
        _hoverNodeId = null;
        UpdateCurrentTextAlignmentFromSelection();
        ZoomToFit();
        RaiseChanged();
        RaiseSelectionChanged();
    }

    public void NewDocument() => LoadDocument(MindMapDocument.CreateStarter());

    public Size GetExportImageSize(double padding)
    {
        EnsureNodeHeights();
        var bounds = DocumentBounds();
        return new Size(
            Math.Max(1, bounds.Width + padding * 2),
            Math.Max(1, bounds.Height + padding * 2));
    }

    public RenderTargetBitmap ExportImage(
        double padding,
        int pixelWidth,
        int pixelHeight,
        bool printColorBackgrounds = true)
    {
        CommitEdit();
        padding = Math.Clamp(padding, 0, 1000);
        EnsureNodeHeights();

        var bounds = DocumentBounds();
        var width = Math.Max(1, bounds.Width + padding * 2);
        var height = Math.Max(1, bounds.Height + padding * 2);
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        var scale = Math.Min(pixelWidth / width, pixelHeight / height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;
        var offsetX = (pixelWidth - drawWidth) / 2;
        var offsetY = (pixelHeight - drawHeight) / 2;

        var bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
        using var ctx = bitmap.CreateDrawingContext();
        ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));

        using (ctx.PushTransform(Matrix.CreateTranslation(offsetX, offsetY)))
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
        using (ctx.PushTransform(Matrix.CreateTranslation(padding - bounds.X, padding - bounds.Y)))
        {
            DrawConnections(ctx);
            DrawNodes(ctx, includeSelection: false, printColorBackgrounds: printColorBackgrounds);
        }

        return bitmap;
    }

    /// <summary>
    /// Parses a Whimsical-style indented outline and drops it onto the page as its own diagram.
    /// The first paste onto a fresh page replaces the empty starter; later pastes are added
    /// below the existing content so several diagrams can share one page.
    /// </summary>
    /// <returns>true if the text produced a map.</returns>
    public bool ImportOutline(string? text)
    {
        var doc = Services.OutlineImporter.Parse(text);
        if (doc == null || doc.IsEmpty) return false;

        if (_doc.IsPristineStarter)
            LoadDocument(doc);
        else
            AddDiagramBelow(doc);
        return true;
    }

    public string ExportOutlineForClipboard()
    {
        CommitEdit();
        if (_doc.IsEmpty) return "";

        var included = _selected.Count > 0
            ? _selected.ToHashSet()
            : _doc.Nodes.Select(n => n.Id).ToHashSet();
        var roots = _doc.Nodes
            .Where(n => included.Contains(n.Id) && !_doc.Connections.Any(c => c.ToId == n.Id && included.Contains(c.FromId)))
            .OrderBy(n => n.Y)
            .ThenBy(n => n.X)
            .ToList();
        var sb = new StringBuilder();
        foreach (var root in roots)
            AppendOutlineNode(root, included, 0, sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Appends another diagram, translated to sit just below the current content.</summary>
    private void AddDiagramBelow(MindMapDocument incoming)
    {
        CancelEdit();
        PushUndo();

        double exMinX = _doc.Nodes.Min(n => n.X);
        double exMaxY = _doc.Nodes.Max(n => n.Y + n.Height);
        double inMinX = incoming.Nodes.Min(n => n.X);
        double inMinY = incoming.Nodes.Min(n => n.Y);

        double dx = exMinX - inMinX;
        double dy = (exMaxY + 100) - inMinY;
        foreach (var n in incoming.Nodes)
        {
            n.X += dx;
            n.Y += dy;
        }

        _doc.Nodes.AddRange(incoming.Nodes);
        _doc.Connections.AddRange(incoming.Connections);

        _selected.Clear();
        foreach (var n in incoming.Nodes) _selected.Add(n.Id);
        UpdateCurrentTextAlignmentFromSelection();

        ZoomToFit();
        RaiseChanged();
        RaiseSelectionChanged();
    }

    public void DeleteSelection()
    {
        if (_selected.Count == 0) return;
        PushUndo();
        _doc.Nodes.RemoveAll(n => _selected.Contains(n.Id));
        _doc.Connections.RemoveAll(c => _selected.Contains(c.FromId) || _selected.Contains(c.ToId));
        _selected.Clear();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    public void SetSelectionColor(string hex)
    {
        if (!SelectedNodes().Any()) return;
        PushUndo();
        foreach (var n in SelectedNodes()) n.Color = hex;
        RaiseChanged();
    }

    public void SetSelectionTextAlignment(TextAlignment alignment)
    {
        _currentTextAlignment = alignment;
        var nodes = SelectedNodes().ToList();
        if (nodes.Count == 0)
        {
            RaiseSelectionChanged();
            return;
        }

        var next = AlignmentToString(alignment);
        if (nodes.All(n => string.Equals(n.TextAlignment, next, StringComparison.OrdinalIgnoreCase)))
        {
            RaiseSelectionChanged();
            return;
        }

        PushUndo();
        foreach (var n in nodes) n.TextAlignment = next;
        if (_editingNodeId != null) _editor.TextAlignment = alignment;
        RaiseChanged();
        RaiseSelectionChanged();
    }

    public void AddNodeAtCenter()
    {
        PushUndo();
        var world = ToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));
        var node = NewNode(world.X - 75, world.Y - 23);
        _doc.Nodes.Add(node);
        SelectOnly(node.Id);
        RaiseSelectionChanged();
        EnsureNodeVisible(node);
        RaiseChanged();
        BeginEdit(node, isNew: true);
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.15);
    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / 1.15);

    public void RebuildLayout()
    {
        if (_doc.IsEmpty) return;

        CancelEdit();
        PushUndo();
        foreach (var root in _doc.Nodes.Where(n => !_doc.Connections.Any(c => c.ToId == n.Id)).ToList())
        {
            ReflowTree(root);
        }
        RaiseChanged();
    }

    public void ResetZoom()
    {
        _zoom = 1.0;
        _layer.InvalidateVisual();
        RaiseZoom();
    }

    public void ZoomToFit()
    {
        if (_doc.IsEmpty || Bounds.Width < 10 || Bounds.Height < 10)
        {
            _zoom = 1; _panX = 0; _panY = 0;
            _layer.InvalidateVisual(); RaiseZoom();
            return;
        }

        double minX = _doc.Nodes.Min(n => n.X);
        double minY = _doc.Nodes.Min(n => n.Y);
        double maxX = _doc.Nodes.Max(n => n.X + n.Width);
        double maxY = _doc.Nodes.Max(n => n.Y + n.Height);

        const double margin = 80;
        double w = maxX - minX + margin * 2;
        double h = maxY - minY + margin * 2;
        // Never magnify past 100% when fitting (a lone node shouldn't fill the screen).
        _zoom = Math.Clamp(Math.Min(Bounds.Width / w, Bounds.Height / h), 0.2, 1.0);
        _panX = (Bounds.Width - (maxX + minX) * _zoom) / 2;
        _panY = (Bounds.Height - (maxY + minY) * _zoom) / 2;
        _layer.InvalidateVisual();
        RaiseZoom();
    }

    // ---------------------------------------------------------------- Coordinates

    private Point ToWorld(Point s) => new((s.X - _panX) / _zoom, (s.Y - _panY) / _zoom);
    private Point ToScreen(Point w) => new(w.X * _zoom + _panX, w.Y * _zoom + _panY);

    private MindMapNode? NodeById(string id) => _doc.Nodes.FirstOrDefault(n => n.Id == id);
    private IEnumerable<MindMapNode> SelectedNodes() => _doc.Nodes.Where(n => _selected.Contains(n.Id));

    private MindMapNode? HitTestNode(Point world)
    {
        // Reverse so the topmost (last drawn) node wins.
        for (int i = _doc.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _doc.Nodes[i];
            if (world.X >= n.X && world.X <= n.X + n.Width &&
                world.Y >= n.Y && world.Y <= n.Y + n.Height)
                return n;
        }
        return null;
    }

    private MindMapNode? HitTestNode(Point world, HashSet<string> excluded)
    {
        // Reverse so the topmost (last drawn) node wins.
        for (int i = _doc.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _doc.Nodes[i];
            if (excluded.Contains(n.Id)) continue;
            if (world.X >= n.X && world.X <= n.X + n.Width &&
                world.Y >= n.Y && world.Y <= n.Y + n.Height)
                return n;
        }
        return null;
    }

    private bool HandleHit(MindMapNode n, Point world)
    {
        double hx = n.X + n.Width;
        double hy = n.Y + n.Height / 2;
        double r = 12 / _zoom;
        return (world.X - hx) * (world.X - hx) + (world.Y - hy) * (world.Y - hy) <= r * r;
    }

    // ---------------------------------------------------------------- Selection helpers

    private void SelectOnly(string id)
    {
        _selected.Clear();
        _selected.Add(id);
    }

    // ---------------------------------------------------------------- Pointer input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _pressScreen = screen;
        _pressWorld = world;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Pan: right button, middle button, or Space + left.
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed ||
            (props.IsLeftButtonPressed && _spaceDown))
        {
            _mode = DragMode.Panning;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            CommitEdit();
            var dblNode = HitTestNode(world);
            if (dblNode != null) BeginEdit(dblNode);
            else
            {
                PushUndo();
                var node = NewNode(world.X - 75, world.Y - 23);
                _doc.Nodes.Add(node);
                SelectOnly(node.Id);
                RaiseSelectionChanged();
                EnsureNodeVisible(node);
                RaiseChanged();
                BeginEdit(node, isNew: true);
            }
            e.Handled = true;
            return;
        }

        CommitEdit();

        // Start a connection when grabbing a node's handle.
        var handleNode = _doc.Nodes.LastOrDefault(n => HandleHit(n, world));
        if (handleNode != null)
        {
            _mode = DragMode.Connecting;
            _connectFromId = handleNode.Id;
            _connectCurrentWorld = world;
            e.Pointer.Capture(this);
            e.Handled = true;
            _layer.InvalidateVisual();
            return;
        }

        var hit = HitTestNode(world);
        if (hit != null)
        {
            if (ctrl)
            {
                if (!_selected.Add(hit.Id)) _selected.Remove(hit.Id);
            }
            else if (!_selected.Contains(hit.Id))
            {
                SelectOnly(hit.Id);
            }
            UpdateCurrentTextAlignmentFromSelection();
            RaiseSelectionChanged();

            // Begin moving the whole selection. Snapshot is committed to undo only if it
            // actually moves (see the release handler), so a plain click doesn't record one.
            _mode = DragMode.MovingNodes;
            _pendingMoveSnapshot = _doc.Clone();
            _dragPrimaryNodeId = hit.Id;
            _moveOrigin.Clear();
            foreach (var n in NodesToMoveForDrag(hit.Id)) _moveOrigin[n.Id] = new Point(n.X, n.Y);
        }
        else
        {
            if (!ctrl) _selected.Clear();
            UpdateCurrentTextAlignmentFromSelection();
            RaiseSelectionChanged();
            _mode = DragMode.Marquee;
            _marquee = new Rect(world, world);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        _layer.InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);

        switch (_mode)
        {
            case DragMode.Panning:
                _panX += screen.X - _pressScreen.X;
                _panY += screen.Y - _pressScreen.Y;
                _pressScreen = screen;
                UpdateEditorPosition();
                _layer.InvalidateVisual();
                return;

            case DragMode.MovingNodes:
            {
                double dx = world.X - _pressWorld.X;
                double dy = world.Y - _pressWorld.Y;
                foreach (var kv in _moveOrigin)
                {
                    var n = NodeById(kv.Key);
                    if (n == null) continue;
                    n.X = kv.Value.X + dx;
                    n.Y = kv.Value.Y + dy;
                }
                _dropParentCandidateId = FindReparentCandidate(world)?.Id;
                UpdateEditorPosition();
                _layer.InvalidateVisual();
                return;
            }

            case DragMode.Marquee:
                _marquee = new Rect(_pressWorld, world);
                _layer.InvalidateVisual();
                return;

            case DragMode.Connecting:
                _connectCurrentWorld = world;
                _layer.InvalidateVisual();
                return;

            default:
            {
                // Hover tracking so the connector handle can highlight.
                var hover = HitTestNode(world)?.Id;
                if (hover != _hoverNodeId)
                {
                    _hoverNodeId = hover;
                    _layer.InvalidateVisual();
                }
                return;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var world = ToWorld(e.GetPosition(this));

        switch (_mode)
        {
            case DragMode.MovingNodes:
            {
                bool moved = _moveOrigin.Any(kv =>
                {
                    var n = NodeById(kv.Key);
                    return n != null && (n.X != kv.Value.X || n.Y != kv.Value.Y);
                });
                bool reparented = TryReparentMovedNode(world);
                bool reflowed = !reparented && TryReflowMovedBranch();
                if ((moved || reparented || reflowed) && _pendingMoveSnapshot != null)
                {
                    _undo.Add(_pendingMoveSnapshot);
                    if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
                }
                _pendingMoveSnapshot = null;
                _dragPrimaryNodeId = null;
                if (moved || reparented || reflowed) RaiseChanged();
                break;
            }

            case DragMode.Marquee:
            {
                var r = _marquee.Normalize();
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) _selected.Clear();
                foreach (var n in _doc.Nodes)
                {
                    var nr = new Rect(n.X, n.Y, n.Width, n.Height);
                    if (r.Intersects(nr)) _selected.Add(n.Id);
                }
                UpdateCurrentTextAlignmentFromSelection();
                RaiseSelectionChanged();
                break;
            }

            case DragMode.Connecting when _connectFromId != null:
            {
                var target = HitTestNode(world);
                if (target != null && target.Id != _connectFromId)
                {
                    AddConnection(_connectFromId, target.Id);
                }
                else if (target == null)
                {
                    // Drop on empty space -> spawn a connected child there.
                    var from = NodeById(_connectFromId);
                    if (from != null)
                    {
                        PushUndo();
                        var child = new MindMapNode
                        {
                            X = world.X - 75,
                            Y = world.Y - 23,
                            Text = "",
                            Color = from.Color,
                            TextAlignment = AlignmentToString(_currentTextAlignment),
                        };
                        _doc.Nodes.Add(child);
                        AddConnection(from.Id, child.Id, recordUndo: false);
                        SelectOnly(child.Id);
                        RaiseSelectionChanged();
                        EnsureNodeVisible(child);
                        RaiseChanged();
                        BeginEdit(child, isNew: true);
                    }
                }
                break;
            }
        }

        _mode = DragMode.None;
        _connectFromId = null;
        _dropParentCandidateId = null;
        _dragPrimaryNodeId = null;
        e.Pointer.Capture(null);
        _layer.InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;

        // Ctrl + wheel: zoom toward the cursor.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
            ZoomAt(e.GetPosition(this), factor);
            return;
        }

        // Otherwise pan: Shift -> horizontal, plain -> vertical.
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            _panX += delta * 60;
        else
            _panY += delta * 60;

        UpdateEditorPosition();
        _layer.InvalidateVisual();
    }

    private void ZoomAt(Point screenAnchor, double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, 0.15, 4.0);
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;
        var worldAnchor = ToWorld(screenAnchor);
        _zoom = newZoom;
        // Keep the point under the cursor fixed.
        _panX = screenAnchor.X - worldAnchor.X * _zoom;
        _panY = screenAnchor.Y - worldAnchor.Y * _zoom;
        CancelEdit();
        _layer.InvalidateVisual();
        RaiseZoom();
    }

    // ---------------------------------------------------------------- Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editingNodeId != null) return; // TextBox owns the keys while editing.

        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                break;
            case Key.Delete:
            case Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.Tab:
                AddChildOfSelected(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Enter:
                AddSiblingOfSelected();
                e.Handled = true;
                break;
            case Key.F2:
            {
                var n = SelectedNodes().FirstOrDefault();
                if (n != null) { BeginEdit(n); e.Handled = true; }
                break;
            }
            case Key.Escape:
                _selected.Clear();
                RaiseSelectionChanged();
                _layer.InvalidateVisual();
                break;
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                PasteRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                CopyRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Undo();
                e.Handled = true;
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SelectAllForCurrentContext();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                ZoomIn(); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract:
                ZoomOut(); e.Handled = true; break;
            case Key.D0 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ZoomToFit(); e.Handled = true; break;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editingNodeId != null || string.IsNullOrEmpty(e.Text)) return;

        var node = SelectedNodes().FirstOrDefault();
        if (node == null) return;

        BeginEdit(node, replacementText: e.Text);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) _spaceDown = false;
    }

    // ---------------------------------------------------------------- Node creation

    private void AddConnection(string fromId, string toId, bool recordUndo = true)
    {
        if (fromId == toId) return;

        // Keep the map a clean hierarchy: reject a link when the two nodes are already
        // related through the tree. This blocks parent -> grandchild (and deeper) shortcuts
        // as well as reverse links that would form a cycle, and any duplicate edge.
        if (IsReachable(fromId, toId) || IsReachable(toId, fromId)) return;

        if (recordUndo) PushUndo();
        _doc.Connections.Add(new MindMapConnection { FromId = fromId, ToId = toId });
        ReflowTree(RootOf(fromId));
        RaiseChanged();
    }

    /// <summary>True if <paramref name="toId"/> can be reached from <paramref name="fromId"/>
    /// by following connections downward (i.e. it is a descendant).</summary>
    private bool IsReachable(string fromId, string toId)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>();
        stack.Push(fromId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            foreach (var c in _doc.Connections)
            {
                if (c.FromId != cur) continue;
                if (c.ToId == toId) return true;
                stack.Push(c.ToId);
            }
        }
        return false;
    }

    private bool TryReparentMovedNode(Point pointerWorld)
    {
        if (_dragPrimaryNodeId == null || !_selected.SetEquals(new[] { _dragPrimaryNodeId })) return false;
        var nodeId = _dragPrimaryNodeId;
        var candidate = _dropParentCandidateId != null
            ? NodeById(_dropParentCandidateId)
            : FindReparentCandidate(pointerWorld);
        return candidate != null && ReparentNode(nodeId, candidate.Id);
    }

    private MindMapNode? FindReparentCandidate(Point pointerWorld)
    {
        if (_dragPrimaryNodeId == null || !_selected.SetEquals(new[] { _dragPrimaryNodeId })) return null;

        var nodeId = _dragPrimaryNodeId;
        var dragged = NodeById(nodeId);
        if (dragged == null) return null;

        var excluded = DescendantIds(nodeId);
        excluded.Add(nodeId);

        var directHit = HitTestNode(pointerWorld, excluded);
        if (directHit != null && CanReparentNode(nodeId, directHit.Id)) return directHit;

        var draggedRect = new Rect(dragged.X, dragged.Y, dragged.Width, dragged.Height);
        return _doc.Nodes
            .Where(n => !excluded.Contains(n.Id) && CanReparentNode(nodeId, n.Id))
            .Select(n => new
            {
                Node = n,
                Overlap = OverlapArea(draggedRect, new Rect(n.X, n.Y, n.Width, n.Height)),
                Distance = DistanceSquared(new Point(dragged.CenterX, dragged.CenterY), new Point(n.CenterX, n.CenterY)),
            })
            .Where(x => x.Overlap / Math.Min(dragged.Width * dragged.Height, x.Node.Width * x.Node.Height) >= ReparentOverlapThreshold)
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => x.Distance)
            .Select(x => x.Node)
            .FirstOrDefault();
    }

    private bool TryReflowMovedBranch()
    {
        if (_dragPrimaryNodeId == null || !_selected.SetEquals(new[] { _dragPrimaryNodeId })) return false;

        var node = NodeById(_dragPrimaryNodeId);
        if (node == null) return false;

        var root = RootOf(node.Id);
        if (root == null || root.Id == node.Id) return false;

        ReflowTree(root);
        return true;
    }

    private IEnumerable<MindMapNode> NodesToMoveForDrag(string primaryNodeId)
    {
        var ids = _selected.ToHashSet();
        if (ids.SetEquals(new[] { primaryNodeId }))
            ids.UnionWith(DescendantIds(primaryNodeId));

        foreach (var node in _doc.Nodes.Where(n => ids.Contains(n.Id)))
            yield return node;
    }

    private bool CanReparentNode(string nodeId, string newParentId)
    {
        if (nodeId == newParentId) return false;
        if (IsReachable(nodeId, newParentId)) return false;

        var currentParent = _doc.Connections.FirstOrDefault(c => c.ToId == nodeId)?.FromId;
        return currentParent != newParentId;
    }

    private bool ReparentNode(string nodeId, string newParentId)
    {
        if (!CanReparentNode(nodeId, newParentId)) return false;

        var node = NodeById(nodeId);
        var newParent = NodeById(newParentId);
        if (node == null || newParent == null) return false;

        var oldRoot = RootOf(nodeId);
        _doc.Connections.RemoveAll(c => c.ToId == nodeId);

        var newConnection = new MindMapConnection { FromId = newParentId, ToId = nodeId };
        var insertIndex = _doc.Connections.FindLastIndex(c => c.FromId == newParentId);
        if (insertIndex >= 0) _doc.Connections.Insert(insertIndex + 1, newConnection);
        else _doc.Connections.Add(newConnection);

        var newRoot = RootOf(newParentId);
        if (oldRoot != null && newRoot?.Id != oldRoot.Id) ReflowTree(oldRoot);
        ReflowTree(newRoot ?? oldRoot);
        return true;
    }

    private HashSet<string> DescendantIds(string nodeId)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>(_doc.Connections.Where(c => c.FromId == nodeId).Select(c => c.ToId));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current)) continue;
            foreach (var child in _doc.Connections.Where(c => c.FromId == current).Select(c => c.ToId))
                stack.Push(child);
        }
        return result;
    }

    private static double OverlapArea(Rect a, Rect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var right = Math.Min(a.Right, b.Right);
        var top = Math.Max(a.Top, b.Top);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private void AddChildOfSelected(bool leftOfRoot = false)
    {
        var parent = SelectedNodes().FirstOrDefault();
        if (parent != null) CreateChildAndEdit(parent.Id, leftOfRoot);
    }

    private void AddSiblingOfSelected()
    {
        var node = SelectedNodes().FirstOrDefault();
        if (node != null) CreateSiblingAndEdit(node.Id);
    }

    /// <summary>Creates a child of <paramref name="parentId"/> and drops straight into edit mode (Tab).</summary>
    private void CreateChildAndEdit(string parentId, bool leftOfRoot = false)
    {
        var parent = NodeById(parentId);
        if (parent == null) return;
        PushUndo();
        var child = CreateChild(parent, leftOfRoot: leftOfRoot);
        SelectOnly(child.Id);
        RaiseSelectionChanged();
        EnsureNodeVisible(child);
        RaiseChanged();
        BeginEdit(child, isNew: true);
    }

    /// <summary>
    /// Creates a sibling of <paramref name="nodeId"/> (a child of its parent) and edits it (Enter).
    /// If the node has no parent (the root), a child is created instead.
    /// </summary>
    private void CreateSiblingAndEdit(string nodeId)
    {
        var node = NodeById(nodeId);
        if (node == null) return;
        PushUndo();
        var parentConn = _doc.Connections.FirstOrDefault(c => c.ToId == nodeId);
        var parent = parentConn != null ? NodeById(parentConn.FromId) : null;
        // Sibling keeps the same column as the node we were editing, stacked just below it.
        var newNode = parent != null ? CreateChild(parent, below: node) : CreateChild(node);
        SelectOnly(newNode.Id);
        RaiseSelectionChanged();
        EnsureNodeVisible(newNode);
        RaiseChanged();
        BeginEdit(newNode, isNew: true);
    }

    private MindMapNode CreateChild(MindMapNode parent, MindMapNode? below = null, bool leftOfRoot = false)
    {
        var child = new MindMapNode
        {
            Text = "",
            Color = parent.Color,
            TextAlignment = AlignmentToString(_currentTextAlignment),
        };
        if (below != null)
        {
            // Keep the reference node's X (its side of the map); drop the new one beneath it.
            child.X = below.X;
            child.Y = below.Y + below.Height + 24;
        }
        else
        {
            // Grow the branch away from the root: children of a left-side node go further left.
            child.X = leftOfRoot && IsRoot(parent)
                ? parent.X - ChildHorizontalGap - child.Width
                : SideOf(parent) < 0
                ? parent.X - ChildHorizontalGap - child.Width
                : parent.X + parent.Width + ChildHorizontalGap;
            child.Y = parent.Y;
        }

        _doc.Nodes.Add(child);
        var connection = new MindMapConnection { FromId = parent.Id, ToId = child.Id };
        if (below != null)
        {
            int belowIndex = _doc.Connections.FindIndex(c => c.FromId == parent.Id && c.ToId == below.Id);
            if (belowIndex >= 0) _doc.Connections.Insert(belowIndex + 1, connection);
            else _doc.Connections.Add(connection);
        }
        else
        {
            _doc.Connections.Add(connection);
        }

        ReflowTree(RootOf(parent.Id));
        return child;
    }

    private void ReflowTree(MindMapNode? root)
    {
        if (root == null) return;

        var directChildren = ChildrenOf(root).ToList();
        if (directChildren.Count == 0) return;

        var left = directChildren.Where(n => n.CenterX < root.CenterX).ToList();
        var right = directChildren.Where(n => n.CenterX >= root.CenterX).ToList();

        LayoutRootSide(root, right, +1);
        LayoutRootSide(root, left, -1);
        UpdateEditorPosition();
    }

    private void EnsureNodeVisible(MindMapNode node)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var topLeft = ToScreen(new Point(node.X, node.Y));
        var bottomRight = ToScreen(new Point(node.X + node.Width, node.Y + node.Height));
        var dx = 0.0;
        var dy = 0.0;

        if (topLeft.X < 0)
            dx = -topLeft.X;
        else if (bottomRight.X > Bounds.Width)
            dx = Bounds.Width - bottomRight.X;

        if (topLeft.Y < 0)
            dy = -topLeft.Y;
        else if (bottomRight.Y > Bounds.Height)
            dy = Bounds.Height - bottomRight.Y;

        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return;

        _panX += dx;
        _panY += dy;
        UpdateEditorPosition();
        _layer.InvalidateVisual();
    }

    private void LayoutRootSide(MindMapNode root, List<MindMapNode> branches, int dir)
    {
        if (branches.Count == 0) return;

        double cursor = 0;
        var laidOut = new List<MindMapNode>();
        foreach (var branch in branches)
        {
            LayoutSubtree(branch, root, dir, ref cursor, laidOut);
        }

        double minY = laidOut.Min(n => n.Y);
        double maxY = laidOut.Max(n => n.Y + n.Height);
        double delta = root.CenterY - (minY + maxY) / 2;
        foreach (var node in laidOut) node.Y += delta;
    }

    private double LayoutSubtree(MindMapNode node, MindMapNode parent, int dir, ref double cursor, List<MindMapNode> laidOut)
    {
        node.X = dir < 0
            ? parent.X - ChildHorizontalGap - node.Width
            : parent.X + parent.Width + ChildHorizontalGap;

        var children = ChildrenOf(node).ToList();
        double centerY;

        if (children.Count == 0)
        {
            centerY = cursor + node.Height / 2;
            cursor += node.Height + ChildVerticalGap;
        }
        else
        {
            double firstChildCenter = 0;
            double lastChildCenter = 0;
            for (int i = 0; i < children.Count; i++)
            {
                double childCenter = LayoutSubtree(children[i], node, dir, ref cursor, laidOut);
                if (i == 0) firstChildCenter = childCenter;
                lastChildCenter = childCenter;
            }
            centerY = (firstChildCenter + lastChildCenter) / 2;
        }

        node.Y = centerY - node.Height / 2;
        laidOut.Add(node);
        return centerY;
    }

    private IEnumerable<MindMapNode> ChildrenOf(MindMapNode parent)
    {
        foreach (var connection in _doc.Connections.Where(c => c.FromId == parent.Id))
        {
            var child = NodeById(connection.ToId);
            if (child != null) yield return child;
        }
    }

    private void AppendOutlineNode(MindMapNode node, HashSet<string> included, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2);
        sb.Append("- ");
        sb.AppendLine(NormalizeOutlineText(node.Text));

        foreach (var child in ChildrenOf(node).Where(n => included.Contains(n.Id)))
            AppendOutlineNode(child, included, depth + 1, sb);
    }

    private static string NormalizeOutlineText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join(" ", normalized.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    /// <summary>The topmost ancestor (a node with no incoming connection).</summary>
    private MindMapNode? RootOf(string nodeId)
    {
        var current = nodeId;
        for (int i = 0; i < 10000; i++)
        {
            var pc = _doc.Connections.FirstOrDefault(c => c.ToId == current);
            if (pc == null) break;
            current = pc.FromId;
        }
        return NodeById(current);
    }

    private bool IsRoot(MindMapNode node) => _doc.Connections.All(c => c.ToId != node.Id);

    /// <summary>-1 if the node sits left of its root, +1 otherwise (root itself defaults right).</summary>
    private double SideOf(MindMapNode node)
    {
        var root = RootOf(node.Id);
        if (root == null || root.Id == node.Id) return 1;
        return node.CenterX < root.CenterX ? -1 : 1;
    }

    // ---------------------------------------------------------------- Editing overlay

    private void BeginEdit(MindMapNode node, bool isNew = false, string? replacementText = null)
    {
        // New nodes were already snapshotted at creation; snapshot before editing an
        // existing node so its text change can be undone.
        if (!isNew) PushUndo();
        _editingNodeId = node.Id;
        _editingIsNew = isNew;
        _editor.Text = replacementText ?? node.Text;
        _editor.TextAlignment = ParseAlignment(node.TextAlignment);
        _editor.FontSize = 14 * _zoom;
        PositionEditor(node);
        _editor.IsVisible = true;
        _layer.InvalidateVisual();
        Dispatcher_Post(() =>
        {
            _editor.Focus();
            if (replacementText == null)
                _editor.SelectAll();
            else
                _editor.CaretIndex = _editor.Text?.Length ?? 0;
            _transitioning = false; // safe to accept LostFocus commits again
        });
    }

    private void PositionEditor(MindMapNode node)
    {
        var tl = ToScreen(new Point(node.X, node.Y));
        SetLeft(_editor, tl.X);
        SetTop(_editor, tl.Y);
        _editor.Width = node.Width * _zoom;
        _editor.MinHeight = node.Height * _zoom;
    }

    private void CommitEdit()
    {
        if (_editingNodeId == null) return;
        var id = _editingNodeId;
        var node = NodeById(id);
        bool wasNew = _editingIsNew;
        _editingNodeId = null;
        _editingIsNew = false;
        _editor.IsVisible = false;

        if (node != null)
        {
            node.Text = _editor.Text ?? "";
            // Discard a freshly-created node the user left empty (Whimsical behaviour).
            bool empty = string.IsNullOrWhiteSpace(node.Text);
            bool hasChildren = _doc.Connections.Any(c => c.FromId == id);
            if (wasNew && empty && !hasChildren && _doc.Nodes.Count > 1)
            {
                var parentConn = _doc.Connections.FirstOrDefault(c => c.ToId == id);
                var root = parentConn != null ? RootOf(parentConn.FromId) : null;
                _doc.Nodes.Remove(node);
                _doc.Connections.RemoveAll(c => c.FromId == id || c.ToId == id);
                _selected.Remove(id);
                RaiseSelectionChanged();
                ReflowTree(root);
            }
        }
        RaiseChanged();
    }

    private void CancelEdit()
    {
        if (_editingNodeId == null) return;
        var id = _editingNodeId;
        bool wasNew = _editingIsNew;
        _editingNodeId = null;
        _editingIsNew = false;
        _editor.IsVisible = false;

        // A brand-new node left empty should not linger.
        if (wasNew && string.IsNullOrWhiteSpace(_editor.Text)
            && _doc.Connections.All(c => c.FromId != id) && _doc.Nodes.Count > 1)
        {
            var parentConn = _doc.Connections.FirstOrDefault(c => c.ToId == id);
            var root = parentConn != null ? RootOf(parentConn.FromId) : null;
            _doc.Nodes.RemoveAll(n => n.Id == id);
            _doc.Connections.RemoveAll(c => c.FromId == id || c.ToId == id);
            _selected.Remove(id);
            RaiseSelectionChanged();
            ReflowTree(root);
        }
        _layer.InvalidateVisual();
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter: commit, then create + edit a sibling (Whimsical outline entry).
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            bool hasText = !string.IsNullOrWhiteSpace(_editor.Text);
            var id = _editingNodeId;
            _transitioning = true;
            CommitEdit();
            if (hasText && id != null && NodeById(id) != null) CreateSiblingAndEdit(id);
            else { _transitioning = false; Focus(); }
            e.Handled = true;
        }
        // Tab: commit, then create + edit a child (go one level deeper).
        else if (e.Key == Key.Tab)
        {
            bool hasText = !string.IsNullOrWhiteSpace(_editor.Text);
            var id = _editingNodeId;
            bool leftOfRoot = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _transitioning = true;
            CommitEdit();
            if (hasText && id != null && NodeById(id) != null) CreateChildAndEdit(id, leftOfRoot);
            else { _transitioning = false; Focus(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Keep whatever was typed (empty new nodes are dropped by CommitEdit).
            _transitioning = true;
            CommitEdit();
            _transitioning = false;
            Focus();
            e.Handled = true;
        }
    }

    private static void Dispatcher_Post(Action a) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background);

    // ---------------------------------------------------------------- Rendering

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layer.Width = finalSize.Width;
        _layer.Height = finalSize.Height;
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Hit-transparent child that hosts the custom drawing (Panel.Render is sealed).</summary>
    private sealed class DrawLayer : Control
    {
        private readonly MindMapEditor _owner;
        public DrawLayer(MindMapEditor owner) => _owner = owner;
        public override void Render(DrawingContext ctx) => _owner.DrawAll(ctx);
    }

    private void DrawAll(DrawingContext ctx)
    {
        DrawGrid(ctx);

        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panX, _panY)))
        {
            DrawConnections(ctx);
            DrawNodes(ctx);
            DrawConnectionInProgress(ctx);
        }

        DrawMarquee(ctx);
        DrawHud(ctx);
    }

    /// <summary>Re-align the edit overlay with its node. Called from pan/move code paths,
    /// never from Render (mutating layout during a render pass causes a layout cycle).</summary>
    private void UpdateEditorPosition()
    {
        if (_editingNodeId == null) return;
        var n = NodeById(_editingNodeId);
        if (n != null) PositionEditor(n);
    }

    private void DrawGrid(DrawingContext ctx)
    {
        const double step = 40;
        double s = step * _zoom;
        if (s < 8) return; // too dense to be useful

        var dot = new SolidColorBrush(GridDotColor);
        double startX = _panX % s;
        double startY = _panY % s;
        double r = 1.1;
        int cols = (int)(Bounds.Width / s) + 2;
        int rows = (int)(Bounds.Height / s) + 2;
        if ((long)cols * rows > 12000) return;

        for (int i = 0; i < cols; i++)
        for (int j = 0; j < rows; j++)
        {
            var c = new Point(startX + i * s, startY + j * s);
            ctx.DrawEllipse(dot, null, c, r, r);
        }
    }

    private void DrawConnections(DrawingContext ctx)
    {
        var branchColor = BuildBranchColors();
        var fallback = ConnectionFallbackColor;

        foreach (var c in _doc.Connections)
        {
            var a = NodeById(c.FromId);
            var b = NodeById(c.ToId);
            if (a == null || b == null) continue;

            var col = branchColor.TryGetValue(c.ToId, out var bc) ? bc : fallback;
            var pen = new Pen(new SolidColorBrush(col), 2.5) { LineCap = PenLineCap.Round };

            bool bRight = b.CenterX >= a.CenterX;
            var start = new Point(bRight ? a.X + a.Width : a.X, a.CenterY);
            var end = new Point(bRight ? b.X : b.X + b.Width, b.CenterY);
            double off = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
            var c1 = new Point(start.X + (bRight ? off : -off), start.Y);
            var c2 = new Point(end.X + (bRight ? -off : off), end.Y);

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(start, false);
                g.CubicBezierTo(c1, c2, end);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }
    }

    private static readonly Color[] BranchLineColors =
    {
        PrimaryBranchColor, DangerBranchColor, WarningBranchColor, SuccessBranchColor,
        PurpleBranchColor, TealBranchColor, PinkBranchColor, OrangeBranchColor,
    };

    /// <summary>
    /// Maps every node to the colour of the root-branch it belongs to. Each direct child
    /// of a root starts a branch and gets its own colour; all deeper descendants inherit it.
    /// </summary>
    private Dictionary<string, Color> BuildBranchColors()
    {
        var result = new Dictionary<string, Color>();
        var roots = _doc.Nodes
            .Where(n => _doc.Connections.All(c => c.ToId != n.Id))
            .Select(n => n.Id)
            .ToHashSet();

        // Assign a colour to each branch node (a direct child of a root), in connection order.
        var branchColorOf = new Dictionary<string, Color>();
        int idx = 0;
        foreach (var c in _doc.Connections)
        {
            if (roots.Contains(c.FromId) && !branchColorOf.ContainsKey(c.ToId))
                branchColorOf[c.ToId] = BranchLineColors[idx++ % BranchLineColors.Length];
        }

        foreach (var n in _doc.Nodes)
        {
            var branch = FindBranchNode(n.Id, roots);
            if (branch != null && branchColorOf.TryGetValue(branch, out var col))
                result[n.Id] = col;
        }
        return result;
    }

    /// <summary>Walks up from a node to the branch node (the direct child of a root) above it.</summary>
    private string? FindBranchNode(string nodeId, HashSet<string> roots)
    {
        var current = nodeId;
        for (int i = 0; i < 10000; i++)
        {
            if (roots.Contains(current)) return null; // the node is itself a root
            var pc = _doc.Connections.FirstOrDefault(c => c.ToId == current);
            if (pc == null) return null;
            if (roots.Contains(pc.FromId)) return current; // parent is a root -> current is the branch
            current = pc.FromId;
        }
        return null;
    }

    private void DrawNodes(
        DrawingContext ctx,
        bool includeSelection = true,
        bool printColorBackgrounds = true)
    {
        foreach (var n in _doc.Nodes)
        {
            IBrush fill = printColorBackgrounds
                ? new SolidColorBrush(Color.Parse(n.Color))
                : Brushes.White;
            bool selected = includeSelection && _selected.Contains(n.Id);
            bool dropParentCandidate = includeSelection && n.Id == _dropParentCandidateId;

            // Auto-grow height to fit wrapped text.
            var ft = MakeText(n);
            double needed = ft.Height + 20;
            if (needed > n.Height) n.Height = needed;

            var rect = new Rect(n.X, n.Y, n.Width, n.Height);
            // Light fills (e.g. white) need a contrasting outline so the node stays visible.
            var borderColor = !printColorBackgrounds
                ? EditorBorderColor
                : selected
                ? EditorBorderColor
                : dropParentCandidate
                ? EditorBorderColor
                : (Luminance(n.Color) > 0.85 ? LightNodeBorderColor : Color.Parse(n.Color));
            var pen = new Pen(new SolidColorBrush(borderColor), selected || dropParentCandidate ? 3 : 1.5);
            ctx.DrawRectangle(fill, pen, rect, 10, 10);

            if (!includeSelection || n.Id != _editingNodeId)
            {
                var origin = new Point(n.X + 10, n.Y + (n.Height - ft.Height) / 2);
                ctx.DrawText(printColorBackgrounds ? ft : MakeText(n, forceBlack: true), origin);
            }

            // Connector handle when hovered or selected.
            if (includeSelection && (n.Id == _hoverNodeId || selected))
            {
                var hc = new Point(n.X + n.Width, n.CenterY);
                ctx.DrawEllipse(Brushes.White, pen, hc, 6, 6);
                ctx.DrawEllipse(new SolidColorBrush(borderColor), null, hc, 2.5, 2.5);
            }
        }
    }

    private Rect DocumentBounds()
    {
        if (_doc.IsEmpty) return new Rect(0, 0, 1, 1);

        var minX = _doc.Nodes.Min(n => n.X);
        var minY = _doc.Nodes.Min(n => n.Y);
        var maxX = _doc.Nodes.Max(n => n.X + n.Width);
        var maxY = _doc.Nodes.Max(n => n.Y + n.Height);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private void EnsureNodeHeights()
    {
        foreach (var n in _doc.Nodes)
        {
            var needed = MakeText(n).Height + 20;
            if (needed > n.Height) n.Height = needed;
        }
    }

    private FormattedText MakeText(MindMapNode n, bool forceBlack = false)
    {
        var text = string.IsNullOrEmpty(n.Text) ? " " : n.Text;
        var brush = forceBlack ? Brushes.Black : ContrastBrush(n.Color);
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif"),
            14,
            brush)
        {
            MaxTextWidth = n.Width - 20,
            TextAlignment = ParseAlignment(n.TextAlignment),
        };
    }

    private MindMapNode NewNode(double x, double y) => new()
    {
        X = x,
        Y = y,
        Text = "",
        TextAlignment = AlignmentToString(_currentTextAlignment),
    };

    private void UpdateCurrentTextAlignmentFromSelection()
    {
        var node = SelectedNodes().FirstOrDefault();
        if (node != null)
            _currentTextAlignment = ParseAlignment(node.TextAlignment);
    }

    private static TextAlignment ParseAlignment(string? alignment) =>
        string.Equals(alignment, "Center", StringComparison.OrdinalIgnoreCase) ? TextAlignment.Center :
        string.Equals(alignment, "Right", StringComparison.OrdinalIgnoreCase) ? TextAlignment.Right :
        TextAlignment.Left;

    private static string AlignmentToString(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "Center",
        TextAlignment.Right => "Right",
        _ => "Left",
    };

    /// <summary>Perceived luminance of a hex colour, 0 (black) .. 1 (white).</summary>
    private static double Luminance(string hex)
    {
        var c = Color.Parse(hex);
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }

    private static IBrush ContrastBrush(string hex) =>
        Luminance(hex) > 0.6 ? Brushes.Black : Brushes.White;

    private void DrawConnectionInProgress(DrawingContext ctx)
    {
        if (_mode != DragMode.Connecting || _connectFromId == null) return;
        var from = NodeById(_connectFromId);
        if (from == null) return;
        var start = new Point(from.X + from.Width, from.CenterY);
        var pen = new Pen(new SolidColorBrush(PrimaryBranchColor), 2)
        {
            DashStyle = DashStyle.Dash,
            LineCap = PenLineCap.Round,
        };
        ctx.DrawLine(pen, start, _connectCurrentWorld);
        ctx.DrawEllipse(new SolidColorBrush(PrimaryBranchColor), null, _connectCurrentWorld, 4, 4);
    }

    private void DrawMarquee(DrawingContext ctx)
    {
        if (_mode != DragMode.Marquee) return;
        var r = _marquee.Normalize();
        var tl = ToScreen(r.TopLeft);
        var br = ToScreen(r.BottomRight);
        var screenRect = new Rect(tl, br);
        var fill = new SolidColorBrush(PrimaryBranchColor, 0.12);
        var pen = new Pen(new SolidColorBrush(PrimaryBranchColor), 1);
        ctx.DrawRectangle(fill, pen, screenRect);
    }

    private void DrawHud(DrawingContext ctx)
    {
        var text = new FormattedText(
            "Double-click: new node   •   Drag edge handle: connect   •   Tab: child   •   Shift+Tab: root child left   •   Enter: sibling   •   Del: delete   •   Wheel: pan (Shift: sideways, Ctrl: zoom)   •   Right-drag: pan",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif"), 11,
            new SolidColorBrush(HudTextColor));
        ctx.DrawText(text, new Point(12, Bounds.Height - 22));
    }

    // ---------------------------------------------------------------- Events

    private void RaiseChanged()
    {
        _layer.InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseZoom() => ZoomChanged?.Invoke(this, EventArgs.Empty);
}
