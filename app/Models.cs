using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MemoryStudio;

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
public sealed record ResultRow(ulong Address, byte[] RawValue, string ValueText, string TypeLabel)
{
    public string AddressText => $"0x{Address:X16}";
}
public sealed class WatchRow : ObservableObject
{
    private bool _isFrozen;
    private string _description = "未命名地址";
    private string _valueText = "—";
    public ulong Address { get; init; }
    public int Type { get; init; }
    public int Size { get; init; }
    public byte[] FrozenValue { get; set; } = [];
    public string AddressText => $"0x{Address:X16}";
    public string TypeLabel => ValueCodec.TypeLabel(Type);
    public bool IsFrozen { get => _isFrozen; set => Set(ref _isFrozen, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
}
public sealed record TableFile(int Version, string ProcessName, List<TableEntry> Entries);
public sealed record TableEntry(string Address, int Type, int Size, string Description);
