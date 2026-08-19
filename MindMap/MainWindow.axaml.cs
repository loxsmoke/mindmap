using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MindMap.Controls;
using MindMap.Services;
using SkiaSharp;

namespace MindMap;

public partial class MainWindow : Window
{
    private enum SavePromptResult { Save, Discard, Cancel }
    private sealed record ExportTarget(IStorageFile File, string Extension);
    private sealed record ExportSettings(int Quality, int Padding, int Width, int Height, bool PrintColorBackgrounds);

    private MindMapEditor _editor = null!;
    private TextBlock _zoomLabel = null!;
    private TextBlock _status = null!;
    private MenuItem _recentMenu = null!;
    private Border _updateBanner = null!;
    private TextBlock _updateBannerText = null!;
    private ProgressBar _updateProgressBar = null!;
    private Button _installUpdateBtn = null!;
    private Button _downloadUpdateBtn = null!;
    private Button _viewUpdateBtn = null!;
    private Button _dismissUpdateBtn = null!;
    private DispatcherTimer? _updateTimer;
    private ReleaseInfo? _latestRelease;
    private bool _checkingForUpdates;
    private bool _updateBusy;
    private bool _canInstallUpdate;
    private bool _canDownloadUpdate;
    private readonly Dictionary<TextAlignment, Button> _alignmentButtons = new();
    private string? _currentPath;
    private bool _isDirty;
    private bool _suppressDirty;
    private bool _closeConfirmed;
    private readonly List<string> _recentFiles = new();
    private static readonly string RecentFilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MindMap",
        "recent.txt");
    private static readonly string DismissedUpdatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MindMap",
        "dismissed-update.txt");

    private static readonly string[] Palette =
    {
        "#4C6EF5", "#F03E3E", "#F59F00", "#37B24D",
        "#7048E8", "#1098AD", "#E64980", "#495057", "#FFFFFF",
    };
    private static Color TranslucentBorderColor { get; } = Color.Parse("#33000000");
    private static Color ControlBorderColor { get; } = Color.Parse("#D0D5DD");
    private static Color MutedTextColor { get; } = Color.Parse("#667085");
    private static Color PreviewBackgroundColor { get; } = Color.Parse("#F5F6F8");
    private static Color ErrorTextColor { get; } = Color.Parse("#D92D20");
    private static Color IconStrokeColor { get; } = Color.Parse("#1C1E21");
    private static Color LinkTextColor { get; } = Color.Parse("#0969DA");
    private static string AppVersion { get; } =
        FormatDisplayVersion(Assembly.GetExecutingAssembly().GetName().Version);

    private static readonly FilePickerFileType MapFileType = new("Mind map")
    {
        Patterns = new[] { "*.mmap", "*.json" },
    };

    private static readonly FilePickerFileType PngFileType = new("PNG image")
    {
        Patterns = new[] { "*.png" },
        MimeTypes = new[] { "image/png" },
    };

    private static readonly FilePickerFileType JpegFileType = new("JPEG image")
    {
        Patterns = new[] { "*.jpg", "*.jpeg" },
        MimeTypes = new[] { "image/jpeg" },
    };

    public MainWindow()
    {
        InitializeComponent();
        WireUp();
    }

    private void WireUp()
    {
        _editor = this.FindControl<MindMapEditor>("Editor")!;
        _zoomLabel = this.FindControl<TextBlock>("ZoomLabel")!;
        _status = this.FindControl<TextBlock>("StatusLabel")!;
        _recentMenu = this.FindControl<MenuItem>("RecentMenuItem")!;
        _updateBanner = this.FindControl<Border>("UpdateBanner")!;
        _updateBannerText = this.FindControl<TextBlock>("UpdateBannerText")!;
        _updateProgressBar = this.FindControl<ProgressBar>("UpdateProgressBar")!;
        _installUpdateBtn = this.FindControl<Button>("InstallUpdateBtn")!;
        _downloadUpdateBtn = this.FindControl<Button>("DownloadUpdateBtn")!;
        _viewUpdateBtn = this.FindControl<Button>("ViewUpdateBtn")!;
        _dismissUpdateBtn = this.FindControl<Button>("DismissUpdateBtn")!;

        this.FindControl<MenuItem>("NewMenuItem")!.Click += async (_, _) => await OnNew();
        this.FindControl<MenuItem>("OpenMenuItem")!.Click += async (_, _) => await OnOpen();
        this.FindControl<MenuItem>("SaveMenuItem")!.Click += async (_, _) => await OnSave();
        this.FindControl<MenuItem>("ExportMenuItem")!.Click += async (_, _) => await OnExport();
        this.FindControl<MenuItem>("AboutMenuItem")!.Click += async (_, _) => await ShowAboutDialog();
        this.FindControl<MenuItem>("ExitMenuItem")!.Click += (_, _) => Close();
        this.FindControl<MenuItem>("UndoMenuItem")!.Click += (_, _) => { _editor.Undo(); _editor.Focus(); };
        this.FindControl<MenuItem>("SelectAllMenuItem")!.Click += (_, _) =>
        {
            if (_editor.SelectAllForCurrentContext())
                _editor.Focus();
        };
        this.FindControl<MenuItem>("NewRootMenuItem")!.Click += (_, _) => { _editor.AddNodeAtCenter(); _editor.Focus(); };
        this.FindControl<MenuItem>("DeleteMenuItem")!.Click += (_, _) => { _editor.DeleteSelection(); _editor.Focus(); };
        this.FindControl<MenuItem>("CopyOutlineMenuItem")!.Click += async (_, _) => await CopyOutline();
        this.FindControl<MenuItem>("PasteOutlineMenuItem")!.Click += async (_, _) => await PasteOutline();
        this.FindControl<Button>("LayoutBtn")!.Click += (_, _) => { _editor.RebuildLayout(); _editor.Focus(); };
        _editor.CopyRequested += async (_, _) => await CopyOutline();
        _editor.PasteRequested += async (_, _) => await PasteOutline();
        this.FindControl<Button>("ZoomInBtn")!.Click += (_, _) => _editor.ZoomIn();
        this.FindControl<Button>("ZoomOutBtn")!.Click += (_, _) => _editor.ZoomOut();
        this.FindControl<Button>("FitBtn")!.Click += (_, _) => _editor.ZoomToFit();
        _installUpdateBtn.Click += async (_, _) => await InstallUpdateAsync();
        _downloadUpdateBtn.Click += async (_, _) => await DownloadUpdateAsync();
        _viewUpdateBtn.Click += (_, _) => ViewReleaseOnGitHub();
        _dismissUpdateBtn.Click += (_, _) => DismissUpdate();

        LoadRecentFiles();
        BuildRecentMenu();
        BuildSwatches();
        BuildTextAlignmentButtons();
        SetWindowTitle();

        _editor.ZoomChanged += (_, _) => _zoomLabel.Text = $"{_editor.ZoomPercent:0}%";
        _editor.DocumentChanged += (_, _) => OnEditorDocumentChanged();
        _editor.SelectionChanged += (_, _) => UpdateTextAlignmentButtons();
        Closing += OnClosing;

        Opened += async (_, _) =>
        {
            _editor.ZoomToFit();
            _editor.Focus();
            UpdateTextAlignmentButtons();
            UpdateStatus();
            await CheckForUpdatesAsync();
            StartUpdateTimer();
        };
    }

    private void BuildSwatches()
    {
        var host = this.FindControl<StackPanel>("Swatches")!;
        foreach (var hex in Palette)
        {
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Avalonia.Thickness(2, 0),
                CornerRadius = new Avalonia.CornerRadius(11),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderBrush = new SolidColorBrush(TranslucentBorderColor),
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = hex;
            btn.Click += (_, _) =>
            {
                _editor.SetSelectionColor(captured);
                _editor.Focus();
            };
            host.Children.Add(btn);
        }
    }

    private void BuildTextAlignmentButtons()
    {
        var host = this.FindControl<StackPanel>("TextAlignmentButtons")!;
        AddTextAlignmentButton(host, TextAlignment.Left, "Left align");
        AddTextAlignmentButton(host, TextAlignment.Center, "Center align");
        AddTextAlignmentButton(host, TextAlignment.Right, "Right align");
        UpdateTextAlignmentButtons();
    }

    private void AddTextAlignmentButton(StackPanel host, TextAlignment alignment, string tooltip)
    {
        var btn = new Button
        {
            Width = 28,
            Height = 26,
            Margin = new Avalonia.Thickness(2, 0),
            CornerRadius = new Avalonia.CornerRadius(4),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(ControlBorderColor),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextAlignmentIcon(alignment),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.Click += (_, _) =>
        {
            _editor.SetSelectionTextAlignment(alignment);
            _editor.Focus();
            UpdateTextAlignmentButtons();
        };
        _alignmentButtons[alignment] = btn;
        host.Children.Add(btn);
    }

    private void UpdateTextAlignmentButtons()
    {
        foreach (var (alignment, button) in _alignmentButtons)
        {
            var selected = _editor.CurrentTextAlignment == alignment;
            button.BorderBrush = selected
                ? Brushes.Black
                : new SolidColorBrush(ControlBorderColor);
            button.BorderThickness = new Avalonia.Thickness(selected ? 3 : 1);
        }
    }

    private void LoadRecentFiles()
    {
        _recentFiles.Clear();
        if (!File.Exists(RecentFilesPath)) return;

        foreach (var path in File.ReadLines(RecentFilesPath))
        {
            if (_recentFiles.Count >= 5) break;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (_recentFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _recentFiles.Add(path);
        }
    }

    private void SaveRecentFiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecentFilesPath)!);
        File.WriteAllLines(RecentFilesPath, _recentFiles);
    }

    private void AddRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > 5) _recentFiles.RemoveRange(5, _recentFiles.Count - 5);

        SaveRecentFiles();
        BuildRecentMenu();
    }

    private void BuildRecentMenu()
    {
        _recentMenu.Items.Clear();

        if (_recentFiles.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = "None", IsEnabled = false });
            return;
        }

        foreach (var path in _recentFiles)
        {
            var item = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            item.Click += async (_, _) => await OpenRecent(path);
            _recentMenu.Items.Add(item);
        }
    }

    private async Task OnNew()
    {
        if (!await ConfirmSaveChanges()) return;
        _suppressDirty = true;
        try
        {
            _editor.NewDocument();
        }
        finally
        {
            _suppressDirty = false;
        }
        _currentPath = null;
        _isDirty = false;
        SetWindowTitle();
        UpdateStatus();
    }

    private async Task PasteOutline()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = "Clipboard has no text to paste.";
            return;
        }

        if (_editor.ImportOutline(text))
        {
            _currentPath = null;
            SetWindowTitle("pasted outline");
        }
        else
        {
            _status.Text = "Could not parse an outline from the clipboard.";
        }
    }

    private async Task CopyOutline()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var outline = _editor.ExportOutlineForClipboard();
        if (string.IsNullOrWhiteSpace(outline))
        {
            _status.Text = "No nodes to copy.";
            return;
        }

        await clipboard.SetTextAsync(outline);
        _status.Text = "Copied outline to clipboard.";
        _editor.Focus();
    }

    private async Task OnOpen()
    {
        if (!await ConfirmSaveChanges()) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open mind map",
            AllowMultiple = false,
            FileTypeFilter = new[] { MapFileType, FilePickerFileTypes.All },
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        try
        {
            var localPath = file.TryGetLocalPath();
            var json = localPath != null
                ? await File.ReadAllTextAsync(localPath)
                : await ReadPickedFile(file);

            _suppressDirty = true;
            try
            {
                _editor.LoadDocument(MindMapStore.Deserialize(json));
            }
            finally
            {
                _suppressDirty = false;
            }
            _currentPath = localPath;
            _isDirty = false;
            SetWindowTitle(file.Name);
            _status.Text = $"Opened {file.Name}";
            AddRecentFile(localPath);
        }
        catch (Exception ex)
        {
            _status.Text = $"Open failed: {ex.Message}";
        }
    }

    private static async Task<string> ReadPickedFile(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private async Task OpenRecent(string path)
    {
        if (!await ConfirmSaveChanges()) return;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            _suppressDirty = true;
            try
            {
                _editor.LoadDocument(MindMapStore.Deserialize(json));
            }
            finally
            {
                _suppressDirty = false;
            }
            _currentPath = path;
            _isDirty = false;
            var name = Path.GetFileName(path);
            SetWindowTitle(name);
            _status.Text = $"Opened {name}";
            AddRecentFile(path);
        }
        catch (Exception ex)
        {
            _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SaveRecentFiles();
            BuildRecentMenu();
            _status.Text = $"Open recent failed: {ex.Message}";
        }
    }
    private async Task<bool> OnSave()
    {
        if (_currentPath != null) return await SaveToPath(_currentPath);

        var suggestedDirectory = GetSuggestedSaveDirectory();
        var suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(suggestedDirectory);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save mind map",
            SuggestedStartLocation = suggestedFolder,
            SuggestedFileName = GetNextAvailableFileName(suggestedDirectory, GetSuggestedDocumentStem(), ".mmap"),
            DefaultExtension = "mmap",
            FileTypeChoices = new[] { MapFileType },
        });
        if (file == null) return false;

        try
        {
            var json = MindMapStore.Serialize(_editor.GetDocument());
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            _currentPath = file.TryGetLocalPath();
            _isDirty = false;
            SetWindowTitle(file.Name);
            _status.Text = $"Saved {file.Name}";
            AddRecentFile(_currentPath);
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
            return false;
        }
    }

    private string GetSuggestedSaveDirectory()
    {
        var currentDirectory = _currentPath != null ? Path.GetDirectoryName(_currentPath) : null;
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            return currentDirectory;

        var recentDirectory = _recentFiles
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        if (!string.IsNullOrWhiteSpace(recentDirectory))
            return recentDirectory;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
    }

    private string GetSuggestedDocumentStem()
    {
        var doc = _editor.GetDocument();
        var childIds = doc.Connections.Select(c => c.ToId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = doc.Nodes
            .Where(n => !childIds.Contains(n.Id))
            .OrderBy(n => n.Y)
            .ThenBy(n => n.X)
            .FirstOrDefault()
            ?? doc.Nodes.FirstOrDefault();

        return SlugifyFileStem(root?.Text, "mindmap");
    }

    private static string SlugifyFileStem(string? text, string fallback)
    {
        var chars = new List<char>();
        var pendingSeparator = false;

        foreach (var c in text?.Trim().ToLowerInvariant() ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && chars.Count > 0) chars.Add('-');
                chars.Add(c);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = chars.Count > 0;
            }
        }

        return chars.Count == 0 ? fallback : new string(chars.ToArray());
    }

    private static string GetNextAvailableFileName(string directory, string stem, string extension)
    {
        if (!Directory.Exists(directory)) return stem + extension;

        int highest = -1;
        foreach (var path in Directory.EnumerateFiles(directory, stem + "*" + extension))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;

            var suffix = name[stem.Length..];
            if (suffix.Length == 0)
            {
                highest = Math.Max(highest, 0);
            }
            else if (suffix.All(char.IsDigit) && int.TryParse(suffix, out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return highest < 0 ? stem + extension : $"{stem}{highest + 1}{extension}";
    }

    private async Task OnExport()
    {
        var target = await PickExportTarget();
        if (target == null) return;

        var settings = await ShowExportSettingsDialog(target.Extension);
        if (settings == null) return;

        try
        {
            using var bitmap = _editor.ExportImage(
                settings.Padding,
                settings.Width,
                settings.Height,
                settings.PrintColorBackgrounds);
            var localPath = target.File.TryGetLocalPath();
            if (localPath != null)
            {
                SaveExportBitmap(bitmap, localPath, target.Extension, settings.Quality);
            }
            else
            {
                await using var stream = await target.File.OpenWriteAsync();
                SaveExportBitmap(bitmap, stream, target.Extension, settings.Quality);
            }
            _status.Text = $"Exported {target.File.Name}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Export failed: {ex.Message}";
        }
    }

    private async Task<ExportTarget?> PickExportTarget()
    {
        var suggestedDirectory = GetSuggestedSaveDirectory();
        var suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(suggestedDirectory);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Image",
            SuggestedStartLocation = suggestedFolder,
            SuggestedFileName = $"{GetCurrentDocumentStem()}.png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { PngFileType, JpegFileType },
        });
        if (file == null) return null;

        var extension = NormalizeImageExtension(Path.GetExtension(file.Name));
        return new ExportTarget(file, extension);
    }

    private Task<ExportSettings?> ShowExportSettingsDialog(string extension)
    {
        var isJpeg = extension == "jpg";
        const int defaultPadding = 48;
        const double entryWidth = 90;
        var naturalSize = _editor.GetExportImageSize(defaultPadding);
        var aspect = naturalSize.Width / Math.Max(1, naturalSize.Height);

        var dialog = new Window
        {
            Title = "Save Configuration",
            Width = 760,
            MinWidth = 760,
            CanResize = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var qualitySlider = new Slider
        {
            Minimum = 1,
            Maximum = 100,
            Value = isJpeg ? 92 : 100,
            IsEnabled = isJpeg,
        };
        var qualityBox = new TextBox
        {
            Text = isJpeg ? "92" : "100",
            Width = entryWidth,
            IsEnabled = isJpeg,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var paddingBox = new TextBox
        {
            Text = defaultPadding.ToString(),
            Width = entryWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var widthBox = new TextBox
        {
            Text = Math.Ceiling(naturalSize.Width).ToString("0"),
            Width = entryWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var heightBox = new TextBox
        {
            Text = Math.Ceiling(naturalSize.Height).ToString("0"),
            Width = entryWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var sizeText = new TextBlock
        {
            Text = "Calculating...",
            Foreground = new SolidColorBrush(MutedTextColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var printBackgrounds = new CheckBox
        {
            Content = "Print color backgrounds",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Image
        {
            Width = 300,
            Height = 220,
            Stretch = Stretch.Uniform,
        };
        var previewHost = new Border
        {
            Width = 320,
            Height = 250,
            Background = new SolidColorBrush(PreviewBackgroundColor),
            BorderBrush = new SolidColorBrush(ControlBorderColor),
            BorderThickness = new Avalonia.Thickness(1),
            Child = preview,
        };
        var message = new TextBlock
        {
            Foreground = new SolidColorBrush(ErrorTextColor),
            TextWrapping = TextWrapping.Wrap,
        };
        var ok = new Button
        {
            Content = "OK",
            MinWidth = 90,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
        };

        var updatingDimensions = false;
        var updateVersion = 0;

        int Quality() => ClampInt(ParseInt(qualityBox.Text, 92), 1, 100);
        int Padding() => ClampInt(ParseInt(paddingBox.Text, defaultPadding), 0, 1000);
        int PixelWidth() => ClampInt(ParseInt(widthBox.Text, (int)Math.Ceiling(naturalSize.Width)), 1, 30000);
        int PixelHeight() => ClampInt(ParseInt(heightBox.Text, (int)Math.Ceiling(naturalSize.Height)), 1, 30000);

        void SetNaturalResolutionForPadding()
        {
            var next = _editor.GetExportImageSize(Padding());
            aspect = next.Width / Math.Max(1, next.Height);
            updatingDimensions = true;
            widthBox.Text = Math.Ceiling(next.Width).ToString("0");
            heightBox.Text = Math.Ceiling(next.Height).ToString("0");
            updatingDimensions = false;
        }

        async void UpdatePreview()
        {
            var version = ++updateVersion;
            await Task.Delay(150);
            if (version != updateVersion) return;

            var width = PixelWidth();
            var height = PixelHeight();
            var previewWidth = Math.Min(700, Math.Max(1, width));
            var previewHeight = Math.Max(1, (int)Math.Round(previewWidth * height / (double)width));
            if (previewHeight > 520)
            {
                previewHeight = 520;
                previewWidth = Math.Max(1, (int)Math.Round(previewHeight * width / (double)height));
            }

            try
            {
                using var sizeBitmap = _editor.ExportImage(
                    Padding(),
                    previewWidth,
                    previewHeight,
                    printBackgrounds.IsChecked == true);
                var encodedLength = EstimateEncodedSize(sizeBitmap, extension, isJpeg ? Quality() : null);
                var pixelRatio = width * height / (double)(previewWidth * previewHeight);
                sizeText.Text = FormatBytes((long)Math.Round(encodedLength * pixelRatio));

                preview.Source = _editor.ExportImage(
                    Padding(),
                    previewWidth,
                    previewHeight,
                    printBackgrounds.IsChecked == true);
                message.Text = "";
            }
            catch (Exception ex)
            {
                message.Text = $"Preview failed: {ex.Message}";
            }
        }

        qualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                qualityBox.Text = ((int)Math.Round(qualitySlider.Value)).ToString();
                UpdatePreview();
            }
        };
        qualityBox.LostFocus += (_, _) =>
        {
            qualityBox.Text = Quality().ToString();
            qualitySlider.Value = Quality();
            UpdatePreview();
        };
        paddingBox.LostFocus += (_, _) =>
        {
            paddingBox.Text = Padding().ToString();
            SetNaturalResolutionForPadding();
            UpdatePreview();
        };
        widthBox.LostFocus += (_, _) =>
        {
            if (updatingDimensions) return;
            updatingDimensions = true;
            widthBox.Text = PixelWidth().ToString();
            heightBox.Text = Math.Max(1, (int)Math.Round(PixelWidth() / aspect)).ToString();
            updatingDimensions = false;
            UpdatePreview();
        };
        heightBox.LostFocus += (_, _) =>
        {
            if (updatingDimensions) return;
            updatingDimensions = true;
            heightBox.Text = PixelHeight().ToString();
            widthBox.Text = Math.Max(1, (int)Math.Round(PixelHeight() * aspect)).ToString();
            updatingDimensions = false;
            UpdatePreview();
        };
        printBackgrounds.IsCheckedChanged += (_, _) => UpdatePreview();

        ok.Click += (_, _) => dialog.Close(new ExportSettings(
            Quality(),
            Padding(),
            PixelWidth(),
            PixelHeight(),
            printBackgrounds.IsChecked == true));
        cancel.Click += (_, _) => dialog.Close(null);

        var settings = new Grid
        {
            RowDefinitions = new RowDefinitions("36,36,36,36,36,36,36,36,36"),
            ColumnDefinitions = new ColumnDefinitions("130,*,Auto"),
            RowSpacing = 8,
            ColumnSpacing = 10,
        };

        AddExportLabel(settings, "Format:", 0);
        AddExportValue(settings, isJpeg ? "JPEG" : "PNG", 0);
        AddExportLabel(settings, "Quality:", 1);
        Grid.SetRow(qualitySlider, 1);
        Grid.SetColumn(qualitySlider, 1);
        Grid.SetColumnSpan(qualitySlider, 2);
        settings.Children.Add(qualitySlider);
        AddExportLabel(settings, "Quality value:", 2);
        Grid.SetRow(qualityBox, 2);
        Grid.SetColumn(qualityBox, 1);
        settings.Children.Add(qualityBox);
        AddExportLabel(settings, "Estimated size:", 3);
        Grid.SetRow(sizeText, 3);
        Grid.SetColumn(sizeText, 1);
        Grid.SetColumnSpan(sizeText, 2);
        settings.Children.Add(sizeText);
        AddExportLabel(settings, "Backgrounds:", 4);
        Grid.SetRow(printBackgrounds, 4);
        Grid.SetColumn(printBackgrounds, 1);
        Grid.SetColumnSpan(printBackgrounds, 2);
        settings.Children.Add(printBackgrounds);
        AddExportLabel(settings, "Padding:", 5);
        Grid.SetRow(paddingBox, 5);
        Grid.SetColumn(paddingBox, 1);
        settings.Children.Add(paddingBox);
        AddExportLabel(settings, "Width:", 6);
        Grid.SetRow(widthBox, 6);
        Grid.SetColumn(widthBox, 1);
        settings.Children.Add(widthBox);
        AddExportLabel(settings, "Height:", 7);
        Grid.SetRow(heightBox, 7);
        Grid.SetColumn(heightBox, 1);
        settings.Children.Add(heightBox);
        Grid.SetRow(message, 8);
        Grid.SetColumn(message, 1);
        Grid.SetColumnSpan(message, 2);
        settings.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new Grid
        {
            Margin = new Avalonia.Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,340"),
            RowSpacing = 16,
            ColumnSpacing = 18,
        };
        Grid.SetRow(settings, 0);
        Grid.SetColumn(settings, 0);
        root.Children.Add(settings);
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);
        Grid.SetRow(previewHost, 0);
        Grid.SetColumn(previewHost, 1);
        root.Children.Add(previewHost);

        dialog.Content = root;
        UpdatePreview();
        return dialog.ShowDialog<ExportSettings?>(this);
    }

    private static void AddExportLabel(Grid form, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        form.Children.Add(label);
    }

    private static void AddExportValue(Grid form, string text, int row)
    {
        var value = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        Grid.SetColumnSpan(value, 2);
        form.Children.Add(value);
    }

    private static string NormalizeImageExtension(string? extension)
    {
        extension = extension?.TrimStart('.').ToLowerInvariant();
        return extension is "jpg" or "jpeg" ? "jpg" : "png";
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    private static int ClampInt(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private static long EstimateEncodedSize(Avalonia.Media.Imaging.Bitmap bitmap, string extension, int? quality)
    {
        using var ms = new MemoryStream();
        SaveExportBitmap(bitmap, ms, extension, quality ?? 100);
        return ms.Length;
    }

    private static void SaveExportBitmap(
        Avalonia.Media.Imaging.Bitmap bitmap,
        string path,
        string extension,
        int quality)
    {
        if (extension == "jpg")
        {
            using var data = EncodeJpeg(bitmap, quality);
            using var output = File.Create(path);
            data.SaveTo(output);
            return;
        }

        bitmap.Save(path);
    }

    private static void SaveExportBitmap(
        Avalonia.Media.Imaging.Bitmap bitmap,
        Stream stream,
        string extension,
        int quality)
    {
        if (extension == "jpg")
        {
            using var data = EncodeJpeg(bitmap, quality);
            data.SaveTo(stream);
            return;
        }

        bitmap.Save(stream);
    }

    private static SKData EncodeJpeg(Avalonia.Media.Imaging.Bitmap bitmap, int quality)
    {
        quality = ClampInt(quality, 1, 100);
        var tempPath = Path.Combine(Path.GetTempPath(), $"mindmap-export-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(tempPath);
            using var skBitmap = SKBitmap.Decode(tempPath);
            return skBitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private string GetCurrentDocumentStem()
    {
        if (!string.IsNullOrWhiteSpace(_currentPath))
            return Path.GetFileNameWithoutExtension(_currentPath);

        return "mindmap";
    }

    private async Task<bool> SaveToPath(string path)
    {
        try
        {
            var json = MindMapStore.Serialize(_editor.GetDocument());
            await File.WriteAllTextAsync(path, json);
            _isDirty = false;
            _currentPath = path;
            var name = Path.GetFileName(path);
            SetWindowTitle(name);
            _status.Text = $"Saved {name}";
            AddRecentFile(path);
            UpdateStatus();
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
            return false;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_isDirty)
        {
            _updateTimer?.Stop();
            return;
        }

        e.Cancel = true;
        if (!await ConfirmSaveChanges()) return;

        _updateTimer?.Stop();
        _closeConfirmed = true;
        Close();
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer != null) return;

        _updateTimer = new DispatcherTimer
        {
            Interval = UpdateCheck.CheckInterval,
        };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        try
        {
            var current = UpdateCheck.ParseTag(AppVersion) ?? new Version(0, 0, 0);
            var release = await UpdateService.CheckAsync(current);
            switch (UpdateCheck.Decide(release, ReadDismissedUpdate()))
            {
                case UpdateCheck.BannerState.Unchanged:
                    break;
                case UpdateCheck.BannerState.Hidden:
                    _latestRelease = null;
                    _updateBanner.IsVisible = false;
                    break;
                case UpdateCheck.BannerState.Shown:
                    _latestRelease = release;
                    _updateBannerText.Text = $"MindMap v{release!.Version} is available.";
                    _canDownloadUpdate = true;
                    _canInstallUpdate = release.SetupUrl is not null && UpdateInstaller.IsInstalledBySetup();
                    UpdateBannerActions();
                    _updateBanner.IsVisible = true;
                    break;
            }
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_latestRelease is not { } release || _updateBusy || !_canInstallUpdate) return;
        var previousText = _updateBannerText.Text;

        var path = await DownloadInstallerAsync(release, UpdateInstaller.UpdateStagingDir());
        if (path is null) return;

        try
        {
            _updateBannerText.Text = $"Installing v{release.Version} - MindMap will restart...";
            UpdateInstaller.Launch(path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _updateBannerText.Text = previousText;
            SetUpdateBusy(false);
            return;
        }
        catch
        {
            _updateBannerText.Text = "Could not start the installer - try Download instead.";
            _canInstallUpdate = false;
            SetUpdateBusy(false);
            UpdateBannerActions();
            return;
        }

        Close();
    }

    private async Task DownloadUpdateAsync()
    {
        if (_latestRelease is not { } release || _updateBusy || !_canDownloadUpdate) return;

        var path = await DownloadInstallerAsync(release, UpdateInstaller.DownloadsFolder());
        if (path is null) return;

        UpdateInstaller.Reveal(path);
        _updateBannerText.Text = $"v{release.Version} saved to Downloads";
    }

    private async Task<string?> DownloadInstallerAsync(ReleaseInfo release, string destDir)
    {
        SetUpdateBusy(true);
        _updateProgressBar.Value = 0;
        _updateBannerText.Text = $"Downloading v{release.Version}...";

        var progress = new Progress<double>(p => _updateProgressBar.Value = p);
        var path = await UpdateInstaller.DownloadAsync(release, destDir, progress);

        if (path is null)
        {
            _updateBannerText.Text = $"Download of v{release.Version} failed - try View on GitHub.";
            _canInstallUpdate = false;
            SetUpdateBusy(false);
            UpdateBannerActions();
            return null;
        }

        SetUpdateBusy(false);
        return path;
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        _updateProgressBar.IsVisible = busy;
        UpdateBannerActions();
    }

    private void UpdateBannerActions()
    {
        _installUpdateBtn.IsVisible = _canInstallUpdate;
        _downloadUpdateBtn.IsVisible = _canDownloadUpdate;

        _installUpdateBtn.IsEnabled = !_updateBusy;
        _downloadUpdateBtn.IsEnabled = !_updateBusy;
        _viewUpdateBtn.IsEnabled = !_updateBusy;
        _dismissUpdateBtn.IsEnabled = !_updateBusy;
    }

    private void ViewReleaseOnGitHub()
    {
        var url = _latestRelease is { } release
            ? Brand.ReleaseTagUrl(release.Version)
            : Brand.ReleasesUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser or blocked shell execution; leave the banner alone.
        }
    }

    private void DismissUpdate()
    {
        if (_latestRelease is { } release)
            WriteDismissedUpdate(release.Version);

        _updateBanner.IsVisible = false;
        _canInstallUpdate = false;
        _canDownloadUpdate = false;
        SetUpdateBusy(false);
    }

    private static string? ReadDismissedUpdate()
    {
        try
        {
            return File.Exists(DismissedUpdatePath)
                ? File.ReadAllText(DismissedUpdatePath).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDismissedUpdate(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DismissedUpdatePath)!);
            File.WriteAllText(DismissedUpdatePath, version);
        }
        catch
        {
            // Dismissal persistence is best-effort.
        }
    }

    private void OnEditorDocumentChanged()
    {
        if (!_suppressDirty) _isDirty = true;
        UpdateStatus();
    }

    private async Task<bool> ConfirmSaveChanges()
    {
        if (!_isDirty) return true;

        var result = await ShowSavePrompt();
        return result switch
        {
            SavePromptResult.Save => await OnSave(),
            SavePromptResult.Discard => true,
            _ => false,
        };
    }

    private Task ShowAboutDialog()
    {
        var dialog = new Window
        {
            Title = "About MindMap",
            Width = 360,
            MinWidth = 360,
            CanResize = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var title = new TextBlock
        {
            Text = BuildAboutTitle(AppVersion),
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };

        var details = new TextBlock
        {
            Text = "A lightweight desktop app for creating and editing mind maps.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(MutedTextColor),
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
        };

        var repositoryLink = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
            Padding = new Avalonia.Thickness(0),
            Content = new TextBlock
            {
                Text = "GitHub",
                Foreground = new SolidColorBrush(LinkTextColor),
                TextDecorations = TextDecorations.Underline,
            },
        };
        ToolTip.SetTip(repositoryLink, "Open GitHub repository");
        repositoryLink.Click += (_, _) => OpenRepositoryUrl();

        var close = new Button
        {
            Content = "Close",
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => dialog.Close();

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                title,
                details,
                repositoryLink,
                close,
            },
        };

        dialog.Content = content;
        return dialog.ShowDialog(this);
    }

    private void OpenRepositoryUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Brand.RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open repository: {ex.Message}";
        }
    }

    private Task<SavePromptResult?> ShowSavePrompt()
    {
        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 420,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var message = new TextBlock
        {
            Text = "Save changes before continuing?",
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var save = new Button { Content = "Save", MinWidth = 90 };
        var discard = new Button { Content = "Don't save", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        save.Click += (_, _) => dialog.Close(SavePromptResult.Save);
        discard.Click += (_, _) => dialog.Close(SavePromptResult.Discard);
        cancel.Click += (_, _) => dialog.Close(SavePromptResult.Cancel);

        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);

        var content = new Grid
        {
            Margin = new Avalonia.Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetRow(message, 0);
        Grid.SetRow(buttons, 1);
        content.Children.Add(message);
        content.Children.Add(buttons);

        dialog.Content = content;

        return dialog.ShowDialog<SavePromptResult?>(this);
    }

    private void UpdateStatus()
    {
        var doc = _editor.GetDocument();
        _status.Text = $"{doc.Nodes.Count} nodes · {doc.Connections.Count} connections"
            + (_currentPath != null ? $" · {_currentPath}" : "");
    }

    private void SetWindowTitle(string? documentName = null)
    {
        Title = BuildWindowTitle(AppVersion, documentName);
    }

    internal static string BuildWindowTitle(string appVersion, string? documentName = null)
    {
        var title = $"MindMap - {appVersion}";
        return string.IsNullOrWhiteSpace(documentName)
            ? title
            : $"{title} - {documentName}";
    }

    internal static string BuildAboutTitle(string appVersion) => $"MindMap v{appVersion}";

    internal static string FormatDisplayVersion(Version? version) => version?.ToString(3) ?? "0.1.0";

    private sealed class TextAlignmentIcon : Control
    {
        private readonly TextAlignment _alignment;

        public TextAlignmentIcon(TextAlignment alignment)
        {
            _alignment = alignment;
            Width = 18;
            Height = 16;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var pen = new Pen(new SolidColorBrush(IconStrokeColor), 2)
            {
                LineCap = PenLineCap.Round,
            };
            var lengths = new[] { 16.0, 11.0, 14.0 };
            for (int i = 0; i < lengths.Length; i++)
            {
                var y = 3 + i * 5;
                var x = _alignment switch
                {
                    TextAlignment.Center => (Bounds.Width - lengths[i]) / 2,
                    TextAlignment.Right => Bounds.Width - lengths[i],
                    _ => 0,
                };
                context.DrawLine(pen, new Avalonia.Point(x, y), new Avalonia.Point(x + lengths[i], y));
            }
        }
    }
}
