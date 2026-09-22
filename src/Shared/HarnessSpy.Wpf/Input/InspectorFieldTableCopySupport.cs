using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HarnessSpy.Core.Models;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Wpf.Input;

public sealed class InspectorFieldTableCopySupport
{
    private readonly DataGrid _grid;

    private InspectorFieldTableCopySupport(DataGrid grid)
    {
        _grid = grid;
        _grid.PreviewKeyDown += OnPreviewKeyDown;
        _grid.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            OnCopyExecuted,
            OnCopyCanExecute));
    }

    public static void Enable(DataGrid grid) => _ = new InspectorFieldTableCopySupport(grid);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (TryCopySelectedRow())
        {
            e.Handled = true;
        }
    }

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _grid.SelectedItem is not null &&
                       TryFormatSelectedRow(out _);
        e.Handled = true;
    }

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (TryCopySelectedRow())
        {
            e.Handled = true;
        }
    }

    private bool TryCopySelectedRow()
    {
        if (!TryFormatSelectedRow(out string text))
        {
            return false;
        }

        Clipboard.SetText(text);
        return true;
    }

    private bool TryFormatSelectedRow(out string text)
    {
        text = string.Empty;
        if (_grid.SelectedItem is null)
        {
            return false;
        }

        if (!TryGetKeyValue(_grid.SelectedItem, out string key, out string? value))
        {
            return false;
        }

        text = $"{key}\t{value}";
        return true;
    }

    private static bool TryGetKeyValue(
        object item,
        out string key,
        out string? value)
    {
        switch (item)
        {
            case PayloadField field:
                key = field.Name;
                value = field.Value;
                return true;
            case SessionDetailRow row:
                key = row.Name;
                value = row.Value;
                return true;
            default:
                key = string.Empty;
                value = null;
                return false;
        }
    }
}
