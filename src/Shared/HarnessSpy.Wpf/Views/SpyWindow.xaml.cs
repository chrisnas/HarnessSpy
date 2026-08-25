using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;
using Microsoft.Win32;

namespace HarnessSpy.Wpf.Views;

public partial class SpyWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly string _productName;
    private string? _lastReplayFolder;
    private TreeNodeViewModel? _contextMenuSessionNode;
    private bool _startupReplayHandled;
    private bool _isApplyingSearchMatch;

    public SpyWindow()
        : this(new SettingsService(), lastReplayFolder: null, "HarnessSpy")
    {
    }

    public SpyWindow(
        SettingsService settingsService,
        string? lastReplayFolder,
        string productName)
    {
        _settingsService = settingsService;
        _lastReplayFolder = lastReplayFolder;
        _productName = productName;
        InitializeComponent();
        Title = $"{productName} Hook Spy";
        Loaded += SpyWindow_Loaded;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void SpyWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupReplayHandled)
        {
            return;
        }

        _startupReplayHandled = true;

        string? startupFolder = _lastReplayFolder;
        if (!string.IsNullOrWhiteSpace(startupFolder) && Directory.Exists(startupFolder))
        {
            await LoadReplayFolderAsync(startupFolder);
            return;
        }

        await PickReplayFolderAsync();
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isApplyingSearchMatch)
        {
            return;
        }

        ViewModel?.SelectNode(e.NewValue as TreeNodeViewModel);
        PayloadTextBox.CaretIndex = 0;
        PayloadTextBox.ScrollToHome();
        FieldsDataGrid.UnselectAll();
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
        if (e.Key == Key.Enter)
        {
            Find(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
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
        }
    }

    private void Find(bool previous)
    {
        NodeSearchMatch? match = ViewModel?.FindNext(previous);
        if (match is null || ViewModel is null)
        {
            return;
        }

        _isApplyingSearchMatch = true;
        try
        {
            FieldsDataGrid.UnselectAll();
            TreeViewItem? treeItem = SelectTreeNode(match.Node);
            ViewModel.SelectNode(match.Node, preserveSearch: true);

            if (ViewModel.HasSelectedFields &&
                match.Target is NodeSearchTarget.FieldName or NodeSearchTarget.FieldValue &&
                match.FieldIndex >= 0 &&
                match.FieldIndex < FieldsDataGrid.Items.Count)
            {
                object fieldRow = FieldsDataGrid.Items[match.FieldIndex];
                FieldsDataGrid.SelectedItem = fieldRow;
                FieldsDataGrid.ScrollIntoView(fieldRow);
            }

            if (treeItem is not null)
            {
                treeItem.Focus();
                Keyboard.Focus(treeItem);
            }
        }
        finally
        {
            _isApplyingSearchMatch = false;
        }
    }

    private TreeViewItem? SelectTreeNode(TreeNodeViewModel node)
    {
        List<TreeNodeViewModel>? path = TreeNodeViewModel.FindAncestorPath(ViewModel?.Roots ?? [], node);
        if (path is null || path.Count == 0)
        {
            return null;
        }

        // Expand only the ancestors of the match, via the view model, before
        // touching the visual tree, so realization below only ever needs to
        // walk this one path instead of the whole tree.
        foreach (TreeNodeViewModel ancestor in path)
        {
            ancestor.IsExpanded = true;
        }

        ItemsControl parent = ObservationTreeView;
        TreeViewItem? container = null;

        foreach (TreeNodeViewModel step in path)
        {
            parent.UpdateLayout();

            if (parent.ItemContainerGenerator.ContainerFromItem(step) is not TreeViewItem item)
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
        container.Focus();
        Keyboard.Focus(container);
        return container;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await PickReplayFolderAsync();
    }

    private void TreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TreeViewItem? item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        _contextMenuSessionNode = item?.DataContext as TreeNodeViewModel;

        bool canDelete = ViewModel?.CanDeleteSessionFiles(_contextMenuSessionNode) == true;
        DeleteSessionFilesMenuItem.IsEnabled = canDelete;

        if (_contextMenuSessionNode is null)
        {
            e.Handled = true;
        }
    }

    private async Task PickReplayFolderAsync()
    {
        OpenFolderDialog dialog = new()
        {
            Title = $"Select {_productName} replay folder"
        };

        if (!string.IsNullOrWhiteSpace(_lastReplayFolder) && Directory.Exists(_lastReplayFolder))
        {
            dialog.InitialDirectory = _lastReplayFolder;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _lastReplayFolder = dialog.FolderName;
        _settingsService.Save(new AppSettings { LastReplayFolder = _lastReplayFolder });

        await LoadReplayFolderAsync(_lastReplayFolder);
    }

    private async Task LoadReplayFolderAsync(string folder)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await ViewModel.LoadFolderAsync(folder);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void DeleteSessionFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuSessionNode is not { } sessionNode ||
            ViewModel is null ||
            !ViewModel.CanDeleteSessionFiles(sessionNode))
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"Delete replay JSON files for session \"{sessionNode.Header}\"?\n\nThis cannot be undone.",
            _productName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!ViewModel.TryDeleteSessionFiles(sessionNode, out int deletedCount, out string? error))
        {
            MessageBox.Show(
                this,
                error ?? $"{_productName} could not delete the selected session files.",
                _productName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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