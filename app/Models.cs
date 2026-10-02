using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MemoryStudio;

public sealed class RecordCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true;
    }
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _executing;
    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _executing = true; Refresh();
        try { await execute(); } finally { _executing = false; Refresh(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record Option(string Label, int Value);
public sealed record ProcessItem(int Id, string Name)
{
    public string DisplayName => $"{Name}  ·  PID {Id}";
}
public sealed class ResultRow : ObservableObject
{
    private ulong _address;
    private int _type, _byteSize;
    private byte[] _rawValue = [];
    private string _valueText = "";
    public ResultRow(ulong address, byte[] rawValue, string valueText, string typeLabel, int? type = null, int? byteSize = null)
    {
        SourceAddress = address;
        int resolvedType = type ?? ValueCodec.Types.First(t => t.Label == typeLabel).Value;
        Apply(address, resolvedType, byteSize ?? Math.Max(rawValue.Length, ValueCodec.Width(resolvedType)), rawValue, valueText);
    }
    public ulong SourceAddress { get; }
    public ulong Address => _address;
    public int Type => _type;
    public int ByteSize => _byteSize;
    public byte[] RawValue => _rawValue;
    public string ValueText => _valueText;
    public string AddressText => $"0x{Address:X16}";
    public string TypeLabel => ValueCodec.TypeLabel(Type);
    public void Apply(ulong address, int type, int byteSize, byte[] bytes, string? valueText = null)
    {
        Set(ref _address, address, nameof(Address)); Notify(nameof(AddressText));
        Set(ref _type, type, nameof(Type)); Notify(nameof(TypeLabel));
        Set(ref _byteSize, byteSize, nameof(ByteSize));
        Set(ref _rawValue, bytes, nameof(RawValue));
        Set(ref _valueText, valueText ?? ValueCodec.Format(type, bytes), nameof(ValueText));
    }
}
public sealed class WatchRow : ObservableObject
{
    private bool _isFrozen;
    private string _description = "未命名地址";
    private string _valueText = "—";
    private ulong _address;
    private int _type, _size;
    private byte[] _frozenValue = [];
    public ulong Address { get => _address; set { if (Set(ref _address, value)) Notify(nameof(AddressText)); } }
    public int Type { get => _type; set { if (Set(ref _type, value)) Notify(nameof(TypeLabel)); } }
    public int Size { get => _size; set { if (Set(ref _size, value)) Notify(nameof(CanFreeze)); } }
    public byte[] FrozenValue { get => _frozenValue; set { if (Set(ref _frozenValue, value)) Notify(nameof(CanFreeze)); } }
    public string AddressText => $"0x{Address:X16}";
    public string TypeLabel => ValueCodec.TypeLabel(Type);
    public bool CanFreeze => Size > 0 && FrozenValue.Length == Size && ValueText != "不可读取";
    public bool IsFrozen { get => _isFrozen; set { if (value && !CanFreeze) { Notify(); return; } Set(ref _isFrozen, value); } }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string ValueText { get => _valueText; set { if (Set(ref _valueText, value)) Notify(nameof(CanFreeze)); } }
}
public sealed record TableFile(int Version, string ProcessName, List<TableEntry> Entries);
public sealed record TableEntry(string Address, int Type, int Size, string Description);
