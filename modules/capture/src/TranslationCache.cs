using System;
using System.Collections.Generic;
using System.Linq;

namespace QingJie {
    // Small text-only cache. No bitmaps, disk files, background timer or OCR model.
    public sealed class TranslationCache {
        sealed class Entry { public string Key; public string[] Value; public long Expires; public int Characters; }
        readonly object gate=new object();
        readonly LinkedList<Entry> entries=new LinkedList<Entry>();
        readonly int maxCharacters,maxEntries;
        readonly long lifetime;
        readonly Func<long> now;
        int characters;
        public TranslationCache(int maxCharacters=65536,int maxEntries=64,int lifetimeSeconds=300,Func<long> clock=null){this.maxCharacters=maxCharacters;this.maxEntries=maxEntries;lifetime=lifetimeSeconds*1000L;now=clock??(()=>System.Diagnostics.Stopwatch.GetTimestamp()*1000/System.Diagnostics.Stopwatch.Frequency);}
        void Remove(LinkedListNode<Entry> node){characters-=node.Value.Characters;entries.Remove(node);}
        void Purge(){long time=now();for(var node=entries.First;node!=null;){var next=node.Next;if(node.Value.Expires<=time)Remove(node);node=next;}}
        public string[] Get(string key){lock(gate){Purge();for(var node=entries.First;node!=null;node=node.Next)if(node.Value.Key==key){var value=(string[])node.Value.Value.Clone();entries.Remove(node);entries.AddLast(node);return value;}return null;}}
        public void Put(string key,string[] value){
            if(value==null||value.Any(string.IsNullOrWhiteSpace))return;
            long size=key.Length+value.Sum(s=>(long)s.Length);if(size>maxCharacters||maxEntries<1)return;
            lock(gate){Purge();for(var node=entries.First;node!=null;){var next=node.Next;if(node.Value.Key==key)Remove(node);node=next;}
                while(entries.Count>0&&(entries.Count>=maxEntries||characters+size>maxCharacters))Remove(entries.First);
                var entry=new Entry{Key=key,Value=(string[])value.Clone(),Expires=now()+lifetime,Characters=(int)size};entries.AddLast(entry);characters+=entry.Characters;
            }
        }
        public int CharacterCount{get{lock(gate){Purge();return characters;}}}
        public int Count{get{lock(gate){Purge();return entries.Count;}}}
    }
}
