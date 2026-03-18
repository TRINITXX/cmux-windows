using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cmux.Core.Models;
using Cmux.Core.Config;
using Cmux.Core.Terminal;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

/// <summary>
/// Recursively renders a SplitNode tree as nested Grid panels with
/// GridSplitters for resizable dividers. Leaf nodes contain TerminalControl instances.
/// </summary>
public class SplitPaneContainer : ContentControl
{
    private SurfaceViewModel? _surface;
    private readonly Dictionary<string, TerminalControl> _terminalCache = [];
    private readonly Dictionary<string, Border> _flashBorders = [];

    // Drag-to-swap state
    private string? _draggedPaneId;
    private Point _paneDragStart;

    public event Action? SearchRequested;

    private static SolidColorBrush GetThemeBrush(string key) =>
        Application.Current.Resources[key] as SolidColorBrush ?? Brushes.Transparent;

    private static Color GetThemeColor(string key) =>
        Application.Current.Resources[key] is Color c ? c : Colors.Transparent;

    public SplitPaneContainer()
    {
        Background = Brushes.Transparent;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SurfaceViewModel oldSurface)
        {
            oldSurface.PropertyChanged -= OnSurfacePropertyChanged;
        }

        // Clear terminal cache when switching surfaces/workspaces
        // This prevents reusing terminals from a different workspace
        _terminalCache.Clear();
        _flashBorders.Clear();

        _surface = e.NewValue as SurfaceViewModel;

        if (_surface != null)
        {
            _surface.PropertyChanged += OnSurfacePropertyChanged;
            Rebuild();
        }
        else
        {
            Content = null;
        }
    }

    private void OnSurfacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurfaceViewModel.RootNode)
            or nameof(SurfaceViewModel.IsZoomed))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
        else if (e.PropertyName is nameof(SurfaceViewModel.FocusedPaneId))
        {
            Dispatcher.BeginInvoke(UpdateFocusState);
        }
    }

    /// <summary>
    /// Updates only focus-related visual state on cached terminals without
    /// rebuilding the entire UI tree.
    /// </summary>
    private void UpdateFocusState()
    {
        if (_surface == null) return;

        // In zoom mode, focus change may require rebuild if the zoomed pane changed
        if (_surface.IsZoomed)
        {
            Rebuild();
            return;
        }

        foreach (var (paneId, terminal) in _terminalCache)
        {
            terminal.IsPaneFocused = paneId == _surface.FocusedPaneId;
        }

        // Flash the newly focused pane
        if (_surface.FocusedPaneId != null
            && _flashBorders.TryGetValue(_surface.FocusedPaneId, out var fb))
        {
            FlashPane(fb);
        }
    }

    private void Rebuild()
    {
        if (_surface == null) return;

        // Zoom mode: show only the focused pane full-size
        if (_surface.IsZoomed && _surface.FocusedPaneId != null)
        {
            var focusedNode = _surface.RootNode.FindNode(_surface.FocusedPaneId);
            if (focusedNode != null)
            {
                Content = BuildLeaf(focusedNode);
                return;
            }
        }

        Content = BuildNode(_surface.RootNode);
    }

    private UIElement BuildNode(SplitNode node)
    {
        if (node.IsLeaf)
        {
            return BuildLeaf(node);
        }

        return BuildSplit(node);
    }

    private UIElement BuildLeaf(SplitNode node)
    {
        if (node.PaneId == null)
            return new Border { Background = Brushes.Transparent };

        var paneId = node.PaneId; // Capture for closures

        // Reuse cached terminal if available (preserves session and scroll position)
        if (!_terminalCache.TryGetValue(paneId, out var terminal))
        {
            terminal = new TerminalControl();
            _terminalCache[paneId] = terminal;
        }
        else
        {
            // Detach from old parent before reusing
            // Terminal could be inside DockPanel (with header) or Border
            var oldParent = System.Windows.Media.VisualTreeHelper.GetParent(terminal) as FrameworkElement;
            
            if (oldParent is DockPanel dockPanel)
            {
                dockPanel.Children.Remove(terminal);
            }
            else if (oldParent is Border border)
            {
                border.Child = null;
            }
            
            // Clear old event handlers to prevent memory leaks and wrong callbacks
            terminal.ClearEventHandlers();
        }

        // Wire up event handlers with closures capturing the current pane ID
        terminal.FocusRequested += () => _surface?.FocusPane(paneId);
        terminal.CommandInterceptRequested += command => _surface?.TryHandlePaneCommand(paneId, command) == true;
        terminal.CommandSubmitted += command => _surface?.RegisterCommandSubmission(paneId, command);
        terminal.ClearRequested += () => _surface?.CapturePaneTranscript(paneId, "clear-terminal");
        terminal.SplitRequested += dir =>
        {
            _surface?.FocusPane(paneId);
            _surface?.SplitFocused(dir);
        };
        terminal.ZoomRequested += () => _surface?.ToggleZoom();
        terminal.ClosePaneRequested += () => _surface?.ClosePane(paneId);
        terminal.SearchRequested += () => SearchRequested?.Invoke();
        terminal.IsPaneFocused = paneId == _surface?.FocusedPaneId;
        terminal.IsSurfaceZoomed = _surface?.IsZoomed == true;

        // Attach the terminal session
        var session = _surface?.GetSession(paneId);
        if (session != null)
            terminal.AttachSession(session);

        // Get pane title (custom name takes precedence over shell title)
        var title = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";

        // Create panel with header
        var panel = new DockPanel { LastChildFill = true };

        // Header bar with title and close button
        var header = new Border
        {
            Background = GetThemeBrush("SidebarItemHoverBrush"),
            Height = 22,
            Padding = new Thickness(8, 2, 8, 2),
        };

        var headerMenu = new ContextMenu();
        var renamePane = new MenuItem { Header = "Rename Pane" };
        renamePane.Click += (_, _) =>
        {
            var currentName = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";
            var prompt = new TextPromptWindow(
                title: "Rename Pane",
                message: "Set a custom name for this pane.",
                defaultValue: currentName)
            {
                Owner = Window.GetWindow(this),
            };

            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.ResponseText))
                _surface?.SetPaneCustomName(paneId, prompt.ResponseText);
        };
        headerMenu.Items.Add(renamePane);

        var resetPaneName = new MenuItem { Header = "Reset Pane Name" };
        resetPaneName.Click += (_, _) => _surface?.SetPaneCustomName(paneId, string.Empty);
        headerMenu.Items.Add(resetPaneName);

        header.ContextMenu = headerMenu;

        DockPanel.SetDock(header, Dock.Top);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Focus indicator
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Close button

        // Focus indicator (shows which pane is focused) — also drag handle for swap
        var focusIndicator = new Border
        {
            Width = 10,
            Height = 18,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(3, 3, 3, 3),
            Background = terminal.IsPaneFocused
                ? GetThemeBrush("AccentBrush")
                : GetThemeBrush("DividerBrush"),
        };
        Grid.SetColumn(focusIndicator, 0);

        // Title text
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = GetThemeBrush("ForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);

        // Inline rename TextBox (hidden by default)
        var renameBox = new TextBox
        {
            FontSize = 11,
            Foreground = GetThemeBrush("ForegroundBrush"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(renameBox, 1);

        // Double-click title to start inline rename
        titleText.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                renameBox.Text = titleText.Text;
                titleText.Visibility = Visibility.Collapsed;
                renameBox.Visibility = Visibility.Visible;
                renameBox.SelectAll();
                renameBox.Focus();
                e.Handled = true;
            }
        };

        // Commit rename helper
        void CommitRename()
        {
            if (renameBox.Visibility != Visibility.Visible) return;
            var newName = renameBox.Text?.Trim();
            renameBox.Visibility = Visibility.Collapsed;
            titleText.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(newName))
                _surface?.SetPaneCustomName(paneId, newName);
        }

        // Cancel rename helper
        void CancelRename()
        {
            renameBox.Visibility = Visibility.Collapsed;
            titleText.Visibility = Visibility.Visible;
        }

        renameBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename();
                e.Handled = true;
            }
        };

        renameBox.LostFocus += (s, e) => CommitRename();

        // Close button
        var closeButton = new Button
        {
            Content = "\u2715",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Foreground = GetThemeBrush("ForegroundDimBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close pane",
        };
        closeButton.Click += (s, e) => _surface?.ClosePane(paneId);
        Grid.SetColumn(closeButton, 2);

        // Drag-to-swap: mouse handlers on the focus indicator
        focusIndicator.Cursor = Cursors.Hand;
        focusIndicator.MouseLeftButtonDown += (s, e) =>
        {
            _draggedPaneId = paneId;
            _paneDragStart = e.GetPosition(this);
            e.Handled = true;
        };
        focusIndicator.MouseMove += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedPaneId == paneId)
            {
                var pos = e.GetPosition(this);
                var diff = pos - _paneDragStart;
                if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
                {
                    var data = new DataObject(DataFormats.StringFormat, paneId);
                    DragDrop.DoDragDrop(focusIndicator, data, DragDropEffects.Move);
                    _draggedPaneId = null;
                }
            }
        };

        // Drop target: the entire pane area (header handles visual feedback)
        header.AllowDrop = true;
        var headerDefaultBg = header.Background;

        // Shared drag handlers — used on both header and content border
        void HandleDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var sourcePaneId = e.Data.GetData(DataFormats.StringFormat) as string;
                if (sourcePaneId != null && sourcePaneId != paneId)
                {
                    e.Effects = DragDropEffects.Move;
                    header.Background = GetThemeBrush("AccentBrush");
                }
                else
                    e.Effects = DragDropEffects.None;
            }
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void HandleDragLeave(DragEventArgs e) => header.Background = headerDefaultBg;

        void HandleDrop(DragEventArgs e)
        {
            header.Background = headerDefaultBg;
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var sourcePaneId = e.Data.GetData(DataFormats.StringFormat) as string;
                if (sourcePaneId != null && sourcePaneId != paneId)
                    _surface?.SwapPanes(sourcePaneId, paneId);
            }
            e.Handled = true;
        }

        header.DragOver += (s, e) => HandleDragOver(e);
        header.DragLeave += (s, e) => HandleDragLeave(e);
        header.Drop += (s, e) => HandleDrop(e);

        headerGrid.Children.Add(focusIndicator);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(renameBox);
        headerGrid.Children.Add(closeButton);
        header.Child = headerGrid;

        // Only show pane header if there are multiple panes
        var leafCount = _surface?.RootNode?.GetLeaves().Count() ?? 1;
        if (leafCount > 1)
            panel.Children.Add(header);
        panel.Children.Add(terminal);

        var focusedAccent = GetThemeColor("AccentColor");
        var contentBorder = new Border
        {
            Child = panel,
            AllowDrop = true,
            BorderBrush = terminal.IsPaneFocused
                ? new SolidColorBrush(Color.FromArgb(153, focusedAccent.R, focusedAccent.G, focusedAccent.B))
                : GetThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
        };
        contentBorder.DragOver += (s, e) => HandleDragOver(e);
        contentBorder.DragLeave += (s, e) => HandleDragLeave(e);
        contentBorder.Drop += (s, e) => HandleDrop(e);

        // Flash overlay border for focus-switch animation
        // Always create a fresh border to avoid "already child of another element" errors
        var flashBorder = new Border
        {
            BorderThickness = new Thickness(0),
            Opacity = 0,
            IsHitTestVisible = false
        };
        _flashBorders[paneId] = flashBorder;

        var wrapper = new Grid();
        wrapper.Children.Add(contentBorder);
        wrapper.Children.Add(flashBorder);

        return wrapper;
    }


    private UIElement BuildSplit(SplitNode node)
    {
        var grid = new Grid();

        if (node.Direction == SplitDirection.Vertical)
        {
            // Left | Right
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(4, GridUnitType.Pixel),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetColumn(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeWE,
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetColumn(second, 2);
                grid.Children.Add(second);
            }
        }
        else
        {
            // Top / Bottom
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(4, GridUnitType.Pixel),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetRow(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeNS,
            };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetRow(second, 2);
                grid.Children.Add(second);
            }
        }

        return grid;
    }

    private void FlashPane(Border flashBorder)
    {
        flashBorder.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        flashBorder.BorderThickness = new Thickness(2);
        flashBorder.Opacity = 1;

        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            flashBorder.Opacity = 0;
            flashBorder.BorderThickness = new Thickness(0);
        };

        flashBorder.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// Updates settings for all cached terminal controls.
    /// </summary>
    public void UpdateAllTerminals(TerminalTheme theme, string fontFamily, int fontSize)
    {
        foreach (var terminal in _terminalCache.Values)
        {
            terminal.UpdateSettings(theme, fontFamily, fontSize);
        }
    }
}
