using System;
using System.Collections.Generic;

namespace DisplayWeave
{
    // Independent of transient activity messages, and keyed to one window/process lifetime.
    internal sealed class FullscreenFailureHistory
    {
        private sealed class Entry
        {
            internal ApplicationIdentity Target;
            internal string Message;
            internal long Sequence;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long sequence;
        internal int Count { get { return entries.Count; } }
        internal string Latest
        {
            get
            {
                Entry latest = null;
                foreach (Entry entry in entries.Values) if (latest == null || entry.Sequence > latest.Sequence) latest = entry;
                return latest == null ? String.Empty : latest.Message;
            }
        }
        internal IList<ApplicationIdentity> Targets
        {
            get
            {
                List<ApplicationIdentity> result = new List<ApplicationIdentity>();
                foreach (Entry entry in entries.Values) result.Add(entry.Target);
                return result;
            }
        }
        internal void Record(ApplicationIdentity target, string message)
        {
            entries[target.Key] = new Entry { Target = target, Message = message, Sequence = ++sequence };
            if (entries.Count <= 128) return;
            Entry oldest = null;
            foreach (Entry entry in entries.Values) if (oldest == null || entry.Sequence < oldest.Sequence) oldest = entry;
            entries.Remove(oldest.Target.Key);
        }
        internal string For(ApplicationIdentity target)
        {
            Entry entry;
            return target != null && entries.TryGetValue(target.Key, out entry) ? entry.Message : String.Empty;
        }
        internal void ClearTarget(ApplicationIdentity target) { if (target != null) entries.Remove(target.Key); }
        internal void ClearProcess(ApplicationIdentity target)
        {
            if (target == null) return;
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in entries)
                if (pair.Value.Target.ProcessKey == target.ProcessKey) remove.Add(pair.Key);
            foreach (string key in remove) entries.Remove(key);
        }
    }
}
