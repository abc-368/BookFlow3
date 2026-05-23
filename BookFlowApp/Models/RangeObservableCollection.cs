using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BookFlow.App.Models
{
    /// <summary>
    /// ObservableCollection that can replace its entire contents while raising a single
    /// <see cref="NotifyCollectionChangedAction.Reset"/> instead of N per-item events.
    ///
    /// The DOM ladder rebuild previously did Clear() + Add() in a loop, which fires
    /// Reset + N Add events per frame — forcing the grid through N layout passes during
    /// volatile markets. ReplaceRange collapses that to one Reset.
    /// </summary>
    public class RangeObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppress;

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppress) base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_suppress) base.OnPropertyChanged(e);
        }

        /// <summary>Replaces all items, raising a single Reset at the end.</summary>
        public void ReplaceRange(IEnumerable<T> items)
        {
            _suppress = true;
            try
            {
                Items.Clear();
                foreach (var item in items) Items.Add(item);
            }
            finally
            {
                _suppress = false;
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
