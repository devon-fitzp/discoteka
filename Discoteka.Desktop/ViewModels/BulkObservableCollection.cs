using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Discoteka.Desktop.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its entire contents with a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification, avoiding the N sequential
/// <c>Add</c> notifications that would otherwise cause Avalonia to invalidate layout N times.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces all items with <paramref name="items"/> and fires a single Reset notification.
    /// </summary>
    public void ResetWith(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
