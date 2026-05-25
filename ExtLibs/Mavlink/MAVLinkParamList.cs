using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

public partial class MAVLink
{
    public class MAVLinkParamList : List<MAVLinkParam>, INotifyPropertyChanged
    {
        ReaderWriterLock locker = new ReaderWriterLock();

        // Phase 10m fork (deep fix): the string indexer used to do a O(n)
        // linear scan + a ReaderWriterLock.AcquireReaderLock kernel call on
        // EVERY lookup. Callers like ConfigRawParams.processToScreen hit this
        // 1500+ times -> O(n^2) = millions of compares + 1500 kernel-heavy
        // lock acquires (each ~30us on Wine). Aux dictionary brings it to
        // O(1). The base List<T> API is preserved so callers that use
        // foreach/ToArray/Count/[int] are unaffected. Direct base.Add /
        // base.Remove bypasses are rare; the indexer get falls back to the
        // legacy linear scan on dict miss so semantics stay correct.
        private readonly Dictionary<string, MAVLinkParam> _byName =
            new Dictionary<string, MAVLinkParam>(StringComparer.Ordinal);

        public int TotalReported { get; set; }

        public int TotalReceived
        {
            get { return this.Count; }
        }

        public MAVLinkParam this[string name]
        {
            get
            {
                if (name == null) return null;
                try
                {
                    locker.AcquireReaderLock(1000);
                    MAVLinkParam cached;
                    lock (_byName)
                    {
                        if (_byName.TryGetValue(name, out cached) && cached != null
                            && cached.Name == name)
                            return cached;
                    }
                    // Fallback: legacy linear scan in case the cache desynced
                    // (e.g. caller used base.Add/base.Remove directly).
                    foreach (var item in this)
                    {
                        if (item.Name == name)
                        {
                            lock (_byName) { _byName[name] = item; }
                            return item;
                        }
                    }
                }
                finally
                {
                    if (locker.IsReaderLockHeld)
                        locker.ReleaseReaderLock();
                }

                return null;
            }

            set
            {
                int index = 0;
                try
                {
                    locker.AcquireWriterLock(1000);
                    foreach (var item in this)
                    {
                        if (item.Name == name)
                        {
                            this[index] = value;
                            lock (_byName) { _byName[name] = value; }
                            OnPropertyChanged();
                            return;
                        }

                        index++;
                    }

                    base.Add(value);
                    if (value?.Name != null)
                        lock (_byName) { _byName[value.Name] = value; }
                }
                finally
                {
                    locker.ReleaseWriterLock();
                }
            }
        }

        // Only works if one param from the name list if found, will fail if multiple list items are found
        // for use in cases of param conversion where the two names will not coexist
        public MAVLinkParam this[string[] names]
        {
            get
            {
                MAVLinkParam item = null;
                foreach (var s in names)
                {
                    MAVLinkParam new_item = this[s];
                    if (new_item != null)
                    {
                        if (item != null)
                        {
                            // found multiple items in list
                            return null;
                        }
                        item = new_item;
                    }
                }
                return item;
            }

            set
            {
                MAVLinkParam item = this[names];
                if (item != null)
                {
                    item = value;
                }
            }

        }

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (MAVLinkParam item in this.ToArray())
                {
                    yield return item.Name;
                }
            }
        }

        public bool ContainsKey(string v)
        {
            if (v == null) return false;
            try
            {
                locker.AcquireReaderLock(1000);
                lock (_byName)
                {
                    if (_byName.ContainsKey(v)) return true;
                }
                foreach (MAVLinkParam item in this)
                {
                    if (item.Name == v)
                    {
                        lock (_byName) { _byName[v] = item; }
                        return true;
                    }
                }
            }
            finally
            {
                if (locker.IsReaderLockHeld)
                    locker.ReleaseReaderLock();
            }

            return false;
        }

        public new void Clear()
        {
            try
            {
                locker.AcquireWriterLock(1000);
                TotalReported = 0;
                base.Clear();
                lock (_byName) { _byName.Clear(); }
            }
            finally
            {
                locker.ReleaseWriterLock();
            }
        }

        public new void Add(MAVLinkParam item)
        {
            try
            {
                locker.AcquireWriterLock(1000);
                this[item.Name] = item;
            }
            finally
            {
                locker.ReleaseWriterLock();
            }
        }

        public new void AddRange(IEnumerable<MAVLinkParam> collection)
        {
            try
            {
                locker.AcquireWriterLock(1000);
                foreach (var item in collection)
                {
                    base.Add(item);
                    if (item?.Name != null)
                        lock (_byName) { _byName[item.Name] = item; }
                }
                OnPropertyChanged();
            }
            finally
            {
                locker.ReleaseWriterLock();
            }
        }

        public static implicit operator Dictionary<string, double>(MAVLinkParamList list)
        {
            var copy = new Dictionary<string, double>();
            try
            {
                list.locker.AcquireReaderLock(1000);
                foreach (MAVLinkParam item in list.ToArray())
                {
                    copy[item.Name] = item.Value;
                }
            }
            finally
            {
                if (list.locker.IsReaderLockHeld)
                    list.locker.ReleaseReaderLock();
            }

            return copy;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
