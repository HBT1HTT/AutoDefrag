using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoDefrag;

/// <summary>磁盘列表中的一行；勾选状态变化会立即回调保存到配置。</summary>
public sealed class DiskItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private readonly Action<DiskItem> _onSelectionChanged;

    public DiskItem(
        string letter, string volumeLabel, string typeText, string detailText,
        string availableText, string totalText, bool isSelected, string? toolTip,
        Action<DiskItem> onSelectionChanged)
    {
        Letter = letter;
        VolumeLabel = volumeLabel;
        TypeText = typeText;
        DetailText = detailText;
        AvailableText = availableText;
        TotalText = totalText;
        ToolTip = toolTip;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Letter { get; }
    public string VolumeLabel { get; }

    /// <summary>通俗的磁盘类型，例如“机械硬盘”。</summary>
    public string TypeText { get; }

    /// <summary>第二行补充信息，例如“NTFS · 每块 4KB”。</summary>
    public string DetailText { get; }

    /// <summary>可用空间，例如“可用 1.2 TB”。</summary>
    public string AvailableText { get; }

    /// <summary>总容量，例如“共 1.8 TB”。</summary>
    public string TotalText { get; }

    public string? ToolTip { get; }

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
