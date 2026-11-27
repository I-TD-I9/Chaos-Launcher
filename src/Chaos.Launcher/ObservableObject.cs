using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chaos.Launcher;

/// <summary>Base class for view models with change notification.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
