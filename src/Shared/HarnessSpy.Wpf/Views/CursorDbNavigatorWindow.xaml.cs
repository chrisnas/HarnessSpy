using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HarnessSpy.Wpf.Input;
using HarnessSpy.Wpf.ViewModels;
using Microsoft.Win32;

namespace HarnessSpy.Wpf.Views;

public partial class CursorDbNavigatorWindow : Window
{
    public CursorDbNavigatorWindow()
    {
        InitializeComponent();
        InspectorFieldTableCopySupport.Enable(ColumnsDataGrid);
    }

    private CursorDbNavigatorViewModel ViewModel =>
        (CursorDbNavigatorViewModel)DataContext;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DatabaseColumn.Width = new GridLength(ViewModel.DatabasePaneWidth);
        RowsColumn.Width = new GridLength(ViewModel.RowsPaneWidth);
        await ViewModel.InitializeAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        ViewModel.SaveLayout(
            DatabaseColumn.ActualWidth,
            RowsColumn.ActualWidth);
        ViewModel.Dispose();
    }

    private async void OpenDatabase_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            DefaultExt = ".vscdb",
            Filter = "VS Code state databases (*.vscdb)|*.vscdb|SQLite databases (*.db;*.sqlite)|*.db;*.sqlite|All files (*.*)|*.*",
            Title = "Open a Cursor state database"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ViewModel.OpenCustomDatabaseAsync(dialog.FileName);
        }
    }

    private async void DiscoverStores_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.DiscoverStoresAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshAsync();

    private void Back_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NavigateBack();

    private void Forward_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NavigateForward();

    private async void SchemaTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CursorSchemaNodeViewModel node)
        {
            await ViewModel.SelectSchemaNodeAsync(node);
        }
    }

    private void WorkspaceFilter_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ViewModel.ApplyWorkspaceFilter();

    private async void MainTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsLoaded &&
            e.Source == sender &&
            ViewModel.SelectedMainTabIndex == 1)
        {
            await ViewModel.EnsurePhysicalStorageLoadedAsync();
        }
    }

    private async void RowsDataGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        await ViewModel.SelectRowAsync(
            RowsDataGrid.SelectedItem as CursorRowViewModel);

    private async void PreviousPage_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.MovePreviousPageAsync();

    private async void NextPage_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.MoveNextPageAsync();

    private async void ApplyFilter_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ApplyFilterAsync();

    private async void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.FilterText = string.Empty;
        await ViewModel.ApplyFilterAsync();
    }

    private async void FilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ViewModel.ApplyFilterAsync();
            e.Handled = true;
        }
    }

    private async void PageSize_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await ViewModel.ChangePageSizeAsync();
        }
    }

    private async void Relation_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid &&
            grid.SelectedItem is CursorRelationViewModel relation)
        {
            await ViewModel.NavigateToRelationAsync(relation);
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            await ViewModel.RefreshAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenDatabase_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            ViewModel.NavigateBack();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            ViewModel.NavigateForward();
            e.Handled = true;
        }
    }
}
