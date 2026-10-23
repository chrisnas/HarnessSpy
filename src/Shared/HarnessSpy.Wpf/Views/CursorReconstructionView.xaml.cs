using System.Windows;
using System.Windows.Controls;
using HarnessSpy.Wpf.Input;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Wpf.Views;

public partial class CursorReconstructionView : UserControl
{
    public CursorReconstructionView()
    {
        InitializeComponent();
        InspectorFieldTableCopySupport.Enable(EvidenceDataGrid);
    }

    private CursorDbNavigatorViewModel ViewModel =>
        (CursorDbNavigatorViewModel)DataContext;

    private async void Tree_SelectionChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CursorReconstructionItemViewModel item)
        {
            await ViewModel.SelectReconstructionItemAsync(item);
        }
    }

    private async void Pipeline_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is ListBox list &&
            list.SelectedItem is CursorReconstructionItemViewModel item)
        {
            await ViewModel.SelectReconstructionItemAsync(item);
        }
    }

    private async void OpenPhysicalSource_Click(
        object sender,
        RoutedEventArgs e) =>
        await ViewModel.OpenReconstructionPhysicalSourceAsync();

    private async void GoToSourceBubble_Click(
        object sender,
        RoutedEventArgs e) =>
        await ViewModel.GoToReconstructionSourceBubbleAsync();
}
