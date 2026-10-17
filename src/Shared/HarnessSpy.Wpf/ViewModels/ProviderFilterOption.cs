using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.ViewModels;

// One selectable harness in the Session Viewer harness filter combobox.
// Toggling IsSelected notifies the owning catalog view model so the tree can
// be re-filtered and the selection persisted.
public sealed class ProviderFilterOption : ObservableObject
{
    private readonly Action<ProviderFilterOption> _onSelectionChanged;
    private bool _isSelected;
    private string _scanStatus = "Not scanned";

    public ProviderFilterOption(
        HookProvider provider,
        string displayName,
        bool isSelected,
        Action<ProviderFilterOption> onSelectionChanged)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(onSelectionChanged);

        Provider = provider;
        DisplayName = displayName;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public HookProvider Provider { get; }

    public string DisplayName { get; }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged(this);
            }
        }
    }

    public void UpdateScanStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ScanStatus = status;
    }
}
