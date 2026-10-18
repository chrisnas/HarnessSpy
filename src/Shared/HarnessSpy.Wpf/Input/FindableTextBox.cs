using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HarnessSpy.Wpf.Input;

public sealed class FindableTextBox : UserControl
{
    private readonly TextBox _findTextBox;
    private readonly TextBlock _statusTextBlock;
    private readonly TextBox _contentTextBox;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(FindableTextBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnTextChanged));

    public FindableTextBox()
    {
        PreviewKeyDown += FindableTextBox_PreviewKeyDown;
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        Border findBar = new()
        {
            Padding = new Thickness(6),
            Background = System.Windows.Media.Brushes.WhiteSmoke,
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        DockPanel findPanel = new();
        Button closeButton = Button("×", (_, _) => CloseFind());
        DockPanel.SetDock(closeButton, Dock.Right);
        findPanel.Children.Add(closeButton);
        Button previousButton = Button("Previous", (_, _) => Find(previous: true));
        DockPanel.SetDock(previousButton, Dock.Right);
        findPanel.Children.Add(previousButton);
        Button nextButton = Button("Next", (_, _) => Find(previous: false));
        DockPanel.SetDock(nextButton, Dock.Right);
        findPanel.Children.Add(nextButton);
        _statusTextBlock = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(_statusTextBlock, Dock.Right);
        findPanel.Children.Add(_statusTextBlock);
        _findTextBox = new TextBox
        {
            MinWidth = 180,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _findTextBox.TextChanged += (_, _) => Find(previous: false, restart: true);
        _findTextBox.PreviewKeyDown += FindTextBox_PreviewKeyDown;
        findPanel.Children.Add(_findTextBox);
        findBar.Child = findPanel;
        root.Children.Add(findBar);

        _contentTextBox = new TextBox();
        _contentTextBox.SetResourceReference(StyleProperty, "RawValueTextBoxStyle");
        _contentTextBox.IsInactiveSelectionHighlightEnabled = true;
        _contentTextBox.Resources[SystemColors.InactiveSelectionHighlightBrushKey] =
            Brushes.DodgerBlue;
        Grid.SetRow(_contentTextBox, 1);
        root.Children.Add(_contentTextBox);

        Content = root;
        FindBar = findBar;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private Border FindBar { get; }

    private static Button Button(string content, RoutedEventHandler handler)
    {
        Button button = new()
        {
            MinWidth = 64,
            Margin = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            Content = content
        };
        button.Click += handler;
        return button;
    }

    private static void OnTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        FindableTextBox control = (FindableTextBox)dependencyObject;
        control._contentTextBox.Text = (string?)e.NewValue ?? string.Empty;
        control.UpdateStatus();
    }

    private void FindableTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenFind();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        _findTextBox.Focus();
        _findTextBox.SelectAll();
        UpdateStatus();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _contentTextBox.Focus();
    }

    private void Find(bool previous, bool restart = false)
    {
        string query = _findTextBox.Text;
        if (query.Length == 0 || _contentTextBox.Text.Length == 0)
        {
            UpdateStatus();
            return;
        }

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int start = restart
            ? previous ? _contentTextBox.Text.Length : 0
            : previous
                ? Math.Max(0, _contentTextBox.SelectionStart - 1)
                : _contentTextBox.SelectionStart +
                    _contentTextBox.SelectionLength;
        int index = previous
            ? _contentTextBox.Text.LastIndexOf(query, start, comparison)
            : _contentTextBox.Text.IndexOf(query, start, comparison);
        if (index < 0)
        {
            index = previous
                ? _contentTextBox.Text.LastIndexOf(query, comparison)
                : _contentTextBox.Text.IndexOf(query, comparison);
        }

        if (index >= 0)
        {
            ShowSelection(index, query.Length);
        }

        UpdateStatus(index);
    }

    private void ShowSelection(int characterIndex, int length)
    {
        characterIndex = Math.Clamp(
            characterIndex,
            0,
            _contentTextBox.Text.Length);
        length = Math.Clamp(
            length,
            0,
            _contentTextBox.Text.Length - characterIndex);
        (int lineIndex, int lineStart) = LogicalLineAt(characterIndex);
        _contentTextBox.SelectionBrush = Brushes.DodgerBlue;
        _contentTextBox.SelectionOpacity = 0.35;
        _contentTextBox.Focus();
        _contentTextBox.ScrollToLine(lineIndex);
        _contentTextBox.ScrollToHorizontalOffset(
            HorizontalOffsetFor(characterIndex, length, lineStart));
        _contentTextBox.UpdateLayout();
        _contentTextBox.Select(characterIndex, length);
        _contentTextBox.UpdateLayout();
        _findTextBox.Focus();
    }

    private double HorizontalOffsetFor(
        int characterIndex,
        int length,
        int lineStart)
    {
        string linePrefix = _contentTextBox.Text.Substring(
            lineStart,
            characterIndex - lineStart);
        string match = _contentTextBox.Text.Substring(characterIndex, length);
        double matchLeft = MeasureWidth(linePrefix);
        double matchRight = matchLeft + MeasureWidth(match);
        double current = _contentTextBox.HorizontalOffset;
        double viewport = _contentTextBox.ViewportWidth;
        const double margin = 12;
        if (matchLeft < current + margin)
        {
            return Math.Max(0, matchLeft - margin);
        }

        if (viewport > 0 && matchRight > current + viewport - margin)
        {
            return Math.Max(0, matchRight - viewport + margin);
        }

        return current;
    }

    private (int LineIndex, int LineStart) LogicalLineAt(
        int characterIndex)
    {
        int lineIndex = 0;
        int lineStart = 0;
        string text = _contentTextBox.Text;
        for (int index = 0; index < characterIndex; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < characterIndex && text[index + 1] == '\n')
                {
                    index++;
                }

                lineIndex++;
                lineStart = index + 1;
            }
            else if (text[index] == '\n')
            {
                lineIndex++;
                lineStart = index + 1;
            }
        }

        return (lineIndex, lineStart);
    }

    private double MeasureWidth(string text)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentCulture,
            _contentTextBox.FlowDirection,
            new Typeface(
                _contentTextBox.FontFamily,
                _contentTextBox.FontStyle,
                _contentTextBox.FontWeight,
                _contentTextBox.FontStretch),
            _contentTextBox.FontSize,
            _contentTextBox.Foreground,
            VisualTreeHelper.GetDpi(_contentTextBox).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void UpdateStatus(int selectedIndex = -1)
    {
        string query = _findTextBox.Text;
        if (query.Length == 0)
        {
            _statusTextBlock.Text = string.Empty;
            return;
        }

        List<int> matches = [];
        for (int index = 0;
             (index = _contentTextBox.Text.IndexOf(
                 query,
                 index,
                 StringComparison.OrdinalIgnoreCase)) >= 0;
             index += query.Length)
        {
            matches.Add(index);
        }

        if (matches.Count == 0)
        {
            _statusTextBlock.Text = "No matches";
            return;
        }

        int current = selectedIndex >= 0
            ? matches.IndexOf(selectedIndex)
            : matches.FindIndex(index =>
                index == _contentTextBox.SelectionStart);
        _statusTextBlock.Text = current >= 0
            ? $"{current + 1} of {matches.Count}"
            : $"{matches.Count} matches";
    }
}
