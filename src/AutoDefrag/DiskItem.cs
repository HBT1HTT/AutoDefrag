using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoDefrag;

/// <summary>磁盘列表中的一行；勾选状态变化会立即回调保存到配置。</summary>
public sealed class DiskItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private readonly Action<DiskItem> _onSelectionChanged;

    public DiskItem(
        string letter, string volumeLabel, string typeText, string freeText, string fragText,
        bool isSelected, Action<DiskItem> onSelectionChanged)
    {
        Letter = letter;
        VolumeLabel = volumeLabel;
        TypeText = typeText;
        FreeText = freeText;
        FragText = fragText;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Letter { get; }
    public string VolumeLabel { get; }
    public string TypeText { get; }
    public string FreeText { get; }
    public string FragText { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            _onSelectionChanged(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
