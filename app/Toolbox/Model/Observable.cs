using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Toolbox.Model;

/// <summary>Base for the objects the forms and lists bind to.</summary>
internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns the field and announces the change; false when nothing changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Tells every binding to re-read: used by forms whose fields depend on each other.</summary>
    protected void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Base for a form's editable copy: any change refreshes the whole form, so
/// sections that depend on another field (the authentication method, the
/// connection type) appear and disappear without bookkeeping.
/// </summary>
internal abstract class FormDraft : Observable
{
    protected bool SetField<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        ChangedAll();
        return true;
    }
}

/// <summary>An entry of a choice list: the value and how it reads.</summary>
internal sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
