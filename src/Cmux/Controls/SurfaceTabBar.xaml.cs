using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using Cmux.Core.Models;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class SurfaceTabBar : UserControl
{
    private SurfaceViewModel? _renamingSurface;
    private Point _dragStartPoint;
    private SurfaceViewModel? _draggingSurface;
    private bool _isDragging;

    public event Action<string>? SearchTextChanged;
    public event Action? NextMatchRequested;
    public event Action? PreviousMatchRequested;

    public SurfaceTabBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.ClaudeStatusService.StatusChanged += OnClaudeStatusChanged;
        };
        Unloaded += (_, _) =>
        {
            App.ClaudeStatusService.StatusChanged -= OnClaudeStatusChanged;
        };
    }

    private void OnClaudeStatusChanged(string paneId, ClaudeStatus oldStatus, ClaudeStatus newStatus)
    {
        Dispatcher.Invoke(() =>
        {
            // Find all tab items and update dots for surfaces containing this pane
            var itemsControl = FindVisualChild<ItemsControl>(this);
            if (itemsControl == null) return;

            foreach (var surfaceVm in itemsControl.Items.OfType<SurfaceViewModel>())
            {
                // Check if this surface contains the pane
                var leaves = surfaceVm.RootNode?.GetLeaves();
                if (leaves == null) continue;
                var hasPaneMatch = leaves.Any(l => l.PaneId == paneId);
                if (!hasPaneMatch) continue;

                // Get worst status across all panes in this surface
                var worstStatus = ClaudeStatus.Idle;
                foreach (var leaf in leaves)
                {
                    if (leaf.PaneId == null) continue;
                    var status = App.ClaudeStatusService.GetStatus(leaf.PaneId);
                    if (status == ClaudeStatus.Working) { worstStatus = ClaudeStatus.Working; break; }
                    if (status == ClaudeStatus.WaitingForInput) worstStatus = ClaudeStatus.WaitingForInput;
                }

                // Find the container for this surface and update dot
                var container = itemsControl.ItemContainerGenerator.ContainerFromItem(surfaceVm);
                if (container == null) continue;
                var dot = FindVisualChild<Ellipse>(container, "StatusDot");
                if (dot == null) continue;

                switch (worstStatus)
                {
                    case ClaudeStatus.Working:
                        dot.Fill = new SolidColorBrush(Colors.LimeGreen);
                        dot.Visibility = Visibility.Visible;
                        break;
                    case ClaudeStatus.WaitingForInput:
                        dot.Fill = new SolidColorBrush(Colors.Orange);
                        dot.Visibility = Visibility.Visible;
                        break;
                    default:
                        dot.Visibility = Visibility.Collapsed;
                        break;
                }
            }
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (name == null || typed.Name == name))
                return typed;
            var found = FindVisualChild<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    public void UpdateMatchCount(int current, int total)
    {
        MatchCount.Text = total > 0 ? $"{current + 1}/{total}" : "";
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        => SearchTextChanged?.Invoke(SearchInput.Text);

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                PreviousMatchRequested?.Invoke();
            else
                NextMatchRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchInput.Text = "";
            var window = Window.GetWindow(this);
            window?.Focus();
            e.Handled = true;
        }
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => PreviousMatchRequested?.Invoke();
    private void NextMatch_Click(object sender, RoutedEventArgs e) => NextMatchRequested?.Invoke();

    private SurfaceViewModel? GetSurfaceFromMenu(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu ctx)
            return ctx.Tag as SurfaceViewModel;
        return null;
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel surface)
        {
            if (e.ClickCount == 2)
            {
                _draggingSurface = null;
                _renamingSurface = surface;
                TabRenameBox.Text = surface.Name;
                TabRenameBox.Visibility = Visibility.Visible;
                TabRenameBox.SelectAll();
                TabRenameBox.Focus();
                e.Handled = true;
                return;
            }

            // Capture drag start for single click only
            _dragStartPoint = e.GetPosition(this);
            _draggingSurface = surface;

            if (DataContext is WorkspaceViewModel workspace)
                workspace.SelectedSurface = surface;
        }
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingSurface == null || _isDragging)
            return;

        var pos = e.GetPosition(this);
        var diff = pos - _dragStartPoint;

        if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
        {
            _isDragging = true;
            DragDrop.DoDragDrop((DependencyObject)sender, _draggingSurface, DragDropEffects.Move);
            _isDragging = false;
            _draggingSurface = null;
        }
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(SurfaceViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border border)
                border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x63, 0x66, 0xF1));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SurfaceViewModel)) is SurfaceViewModel source &&
            sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel target &&
            source != target &&
            DataContext is WorkspaceViewModel workspace)
        {
            var surfaces = workspace.Surfaces;
            var oldIndex = surfaces.IndexOf(source);
            var newIndex = surfaces.IndexOf(target);
            if (oldIndex >= 0 && newIndex >= 0)
                surfaces.Move(oldIndex, newIndex);
        }

        // Reset drop target visual
        if (sender is Border b)
            b.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SurfaceViewModel surface)
        {
            if (DataContext is WorkspaceViewModel workspace)
                workspace.CloseSurface(surface);
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
            workspace.CreateNewSurface();
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface == null) return;
        _renamingSurface = surface;
        TabRenameBox.Text = surface.Name;
        TabRenameBox.Visibility = Visibility.Visible;
        TabRenameBox.SelectAll();
        TabRenameBox.Focus();
    }

    private void FinishTabRename(bool save)
    {
        if (_renamingSurface != null && save)
            _renamingSurface.Name = TabRenameBox.Text;
        _renamingSurface = null;
        TabRenameBox.Visibility = Visibility.Collapsed;
    }

    private void TabRenameBox_LostFocus(object sender, RoutedEventArgs e) => FinishTabRename(true);

    private void TabRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FinishTabRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { FinishTabRename(false); e.Handled = true; }
    }

    private void DuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel ws)
        {
            ws.CreateNewSurface();
            var newSurf = ws.Surfaces[^1];
            var original = GetSurfaceFromMenu(sender);
            if (original != null) newSurf.Name = original.Name + " (copy)";
        }
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitRight();
        }
    }

    private void SplitDown_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitDown();
        }
    }

    private void CloseThisTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
            ws.CloseSurface(surface);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            var others = ws.Surfaces.Where(s => s != surface).ToList();
            foreach (var other in others)
                ws.CloseSurface(other);
        }
    }
}
