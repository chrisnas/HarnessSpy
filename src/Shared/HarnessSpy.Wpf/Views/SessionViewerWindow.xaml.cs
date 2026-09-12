using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Wpf.Views;

public partial class SessionViewerWindow : Window
{
    private static readonly GridLength FavoritesVisibleHeight =
        new(220, GridUnitType.Pixel);

    private SessionTreeNodeViewModel? _contextMenuNode;
    private SessionCatalogViewModel? _observedViewModel;
    private bool _initialRefreshStarted;
    private bool _isApplyingSelection;

    private readonly DispatcherTimer _dashboardOpenTimer;
    private Popup? _pendingDashboardPopup;

    public SessionViewerWindow()
    {
        InitializeComponent();
        Loaded += SessionViewerWindow_Loaded;

        _dashboardOpenTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _dashboardOpenTimer.Tick += DashboardOpenTimer_Tick;
    }

    private SessionCatalogViewModel? ViewModel =>
        DataContext as SessionCatalogViewModel;

    private const string ProductName = "SessionViewer";

    // System menu command IDs must be below 0xF000 and have their low 4 bits
    // clear, since Windows reserves those bits on the wParam it delivers with
    // WM_SYSCOMMAND.
    private const int WM_SYSCOMMAND = 0x0112;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const nuint AboutSystemMenuId = 0x1000;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        IntPtr systemMenu = GetSystemMenu(handle, revert: false);
        AppendMenu(systemMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(systemMenu, MF_STRING, AboutSystemMenuId, $"About {ProductName}");
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WM_SYSCOMMAND &&
            ((nuint)wParam.ToInt64() & 0xFFF0) == AboutSystemMenuId)
        {
            ShowAboutWindow();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowAboutWindow()
    {
        AboutWindow about = new(ProductName, Icon) { Owner = this };
        about.ShowDialog();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(
        IntPtr hMenu,
        uint flags,
        nuint idNewItem,
        string newItem);

    private async void SessionViewerWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialRefreshStarted || ViewModel is null)
        {
            return;
        }

        _initialRefreshStarted = true;
        _observedViewModel = ViewModel;
        _observedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateFavoritesRowHeight();
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(
                $"Session discovery failed: {exception.Message}");
        }
    }

    private void TreeView_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isApplyingSelection)
        {
            return;
        }

        // The favorites and main trees share this handler and mirror the same
        // node instances. Ignore null transitions so deselecting a node in one
        // tree never clears a valid selection made in the other.
        if (e.NewValue is not SessionTreeNodeViewModel node)
        {
            return;
        }

        ViewModel?.SelectNode(node);
    }

    private void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCatalogViewModel.HasFavorites))
        {
            UpdateFavoritesRowHeight();
        }
    }

    private void UpdateFavoritesRowHeight()
    {
        bool hasFavorites = ViewModel?.HasFavorites == true;
        if (hasFavorites)
        {
            if (FavoritesRow.Height.Value <= 0)
            {
                FavoritesRow.Height = FavoritesVisibleHeight;
            }
        }
        else
        {
            FavoritesRow.Height = new GridLength(0, GridUnitType.Pixel);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RefreshAsync();
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e)
    {
        Find(previous: true);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        Find(previous: false);
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        Find(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            Find(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && ViewModel is not null)
        {
            _ = ViewModel.RefreshAsync();
            e.Handled = true;
        }
    }

    private void Find(bool previous)
    {
        SessionTreeNodeViewModel? match =
            ViewModel?.FindNext(previous);
        if (match is null)
        {
            return;
        }

        _isApplyingSelection = true;
        try
        {
            TreeViewItem? item = SelectTreeNode(match);
            if (item is not null)
            {
                item.Focus();
                Keyboard.Focus(item);
            }
        }
        finally
        {
            _isApplyingSelection = false;
        }
    }

    private TreeViewItem? SelectTreeNode(
        SessionTreeNodeViewModel node)
    {
        List<SessionTreeNodeViewModel>? path =
            SessionTreeNodeViewModel.FindAncestorPath(
                ViewModel?.Roots ?? [],
                node);
        if (path is null || path.Count == 0)
        {
            return null;
        }

        foreach (SessionTreeNodeViewModel ancestor in path)
        {
            ancestor.IsExpanded = true;
        }

        ItemsControl parent = SessionTreeView;
        TreeViewItem? container = null;
        foreach (SessionTreeNodeViewModel step in path)
        {
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromItem(step)
                is not TreeViewItem item)
            {
                return null;
            }

            container = item;
            parent = item;
        }

        if (container is null)
        {
            return null;
        }

        container.IsSelected = true;
        container.BringIntoView();
        return container;
    }

    private void TreeView_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(
            e.OriginalSource as DependencyObject);
        _contextMenuNode =
            item?.DataContext as SessionTreeNodeViewModel;
        OpenSessionMenuItem.IsEnabled =
            ViewModel?.CanOpenSession(_contextMenuNode) == true;
        AddToFavoritesMenuItem.IsEnabled =
            ViewModel?.CanAddToFavorites(_contextMenuNode) == true;
        bool canGoToPlan =
            _contextMenuNode?.LinkedPlanNodeId is not null;
        GoToPlanMenuItem.IsEnabled = canGoToPlan;
        GoToPlanMenuItem.Visibility = canGoToPlan
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_contextMenuNode is null)
        {
            e.Handled = true;
        }
    }

    private void GoToPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuNode?.LinkedPlanNodeId is not string targetId ||
            ViewModel is null)
        {
            return;
        }

        SessionTreeNodeViewModel? target = SessionTreeNodeViewModel
            .EnumerateDepthFirst(ViewModel.Roots)
            .FirstOrDefault(node => string.Equals(
                node.StableId,
                targetId,
                StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        _isApplyingSelection = true;
        try
        {
            TreeViewItem? item = SelectTreeNode(target);
            if (item is not null)
            {
                item.Focus();
                Keyboard.Focus(item);
            }
        }
        finally
        {
            _isApplyingSelection = false;
        }
    }

    private void FavoritesTreeView_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(
            e.OriginalSource as DependencyObject);
        _contextMenuNode =
            item?.DataContext as SessionTreeNodeViewModel;
        OpenFavoriteMenuItem.IsEnabled =
            ViewModel?.CanOpenSession(_contextMenuNode) == true;

        if (_contextMenuNode is null)
        {
            e.Handled = true;
        }
    }

    private void AddToFavorites_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddToFavorites(_contextMenuNode);
    }

    private void RemoveFromFavorites_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.RemoveFromFavorites(_contextMenuNode);
    }

    private async void OpenSession_Click(
        object sender,
        RoutedEventArgs e)
    {
        await OpenSessionAsync(_contextMenuNode);
    }

    private async void OpenSelectedSession_Click(
        object sender,
        RoutedEventArgs e)
    {
        await OpenSessionAsync(ViewModel?.SelectedNode);
    }

    private async Task OpenSessionAsync(
        SessionTreeNodeViewModel? node)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            SessionOpenResult result =
                await ViewModel.OpenSessionAsync(node);
            if (!result.Started && !string.IsNullOrWhiteSpace(result.Error))
            {
                MessageBox.Show(
                    this,
                    result.Error,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ViewModel.ReportError(
                $"The session could not be opened: {exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // The node summary is shown in an interactive Popup instead of a WPF
    // ToolTip so the user can move the pointer onto it and use the inner
    // ScrollViewer (a ToolTip dismisses on mouse-leave, leaving its scroll bar
    // unreachable). The row and the popup content share one hover region: any
    // pending close is cancelled whenever the pointer enters either of them.
    private void DashboardRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement row ||
            row.DataContext is not SessionTreeNodeViewModel node ||
            !node.HasDashboardHover)
        {
            return;
        }

        Popup? popup = FindDashboardPopup(row);
        if (popup is null)
        {
            return;
        }

        CancelDashboardPopupClose(popup);

        if (popup.IsOpen)
        {
            return;
        }

        _pendingDashboardPopup = popup;
        _dashboardOpenTimer.Stop();
        _dashboardOpenTimer.Start();
    }

    private void DashboardRow_MouseLeave(object sender, MouseEventArgs e)
    {
        Popup? popup = FindDashboardPopup(sender as FrameworkElement);
        if (popup is not null && ReferenceEquals(popup, _pendingDashboardPopup))
        {
            _dashboardOpenTimer.Stop();
            _pendingDashboardPopup = null;
        }

        ScheduleDashboardPopupClose(popup);
    }

    private void DashboardContent_MouseEnter(object sender, MouseEventArgs e)
    {
        CancelDashboardPopupClose(GetOwningPopup(sender));
    }

    private void DashboardContent_MouseLeave(object sender, MouseEventArgs e)
    {
        ScheduleDashboardPopupClose(GetOwningPopup(sender));
    }

    private void DashboardOpenTimer_Tick(object? sender, EventArgs e)
    {
        _dashboardOpenTimer.Stop();
        if (_pendingDashboardPopup is { } popup)
        {
            popup.IsOpen = true;
            _pendingDashboardPopup = null;
        }
    }

    private static Popup? FindDashboardPopup(FrameworkElement? row)
    {
        if (row?.Parent is Panel panel)
        {
            foreach (object child in panel.Children)
            {
                if (child is Popup popup)
                {
                    return popup;
                }
            }
        }

        return null;
    }

    private static Popup? GetOwningPopup(object sender) =>
        sender is FrameworkElement element
            ? LogicalTreeHelper.GetParent(element) as Popup
            : null;

    private static void ScheduleDashboardPopupClose(Popup? popup)
    {
        if (popup is null)
        {
            return;
        }

        if (popup.Tag is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                popup.IsOpen = false;
            };
            popup.Tag = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private static void CancelDashboardPopupClose(Popup? popup)
    {
        if (popup?.Tag is DispatcherTimer timer)
        {
            timer.Stop();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
