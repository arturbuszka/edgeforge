using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EdgeForge.Ui;

/// <summary>
/// Hand-rolled INotifyPropertyChanged. The repository deliberately avoids
/// containers and frameworks for a graph this small, and an MVVM package would be
/// a much larger dependency than the twenty lines it replaces.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
