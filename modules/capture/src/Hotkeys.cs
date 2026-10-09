using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QingJie {
    public sealed class Shortcut {
        public int Key;
        public int Modifiers;
        public Shortcut Copy(){return new Shortcut {Key=Key,Modifiers=Modifiers};}
        public bool Same(Shortcut other){return other!=null&&other.Key==Key&&other.Modifiers==Modifiers;}
        public bool Valid(){return Key>=0x08&&Key<=0xFE&&Key!=0x1B&&Key!=0x5B&&Key!=0x5C&&Key!=0x10&&Key!=0x11&&Key!=0x12&&(Modifiers&~7)==0&&((Key>=0x70&&Key<=0x87)||(Modifiers&3)!=0);}
        public override string ToString(){return ((Modifiers&2)!=0?"Ctrl+":"")+((Modifiers&1)!=0?"Alt+":"")+((Modifiers&4)!=0?"Shift+":"")+(Key>=0x70&&Key<=0x87?"F"+(Key-0x6F):Key>=0x41&&Key<=0x5A||Key>=0x30&&Key<=0x39?((char)Key).ToString():KeyInterop.KeyFromVirtualKey(Key).ToString());}
        public static Shortcut FromKey(Key key,ModifierKeys modifiers){if(key==System.Windows.Input.Key.LeftCtrl||key==System.Windows.Input.Key.RightCtrl||key==System.Windows.Input.Key.LeftAlt||key==System.Windows.Input.Key.RightAlt||key==System.Windows.Input.Key.LeftShift||key==System.Windows.Input.Key.RightShift)return null;return new Shortcut {Key=KeyInterop.VirtualKeyFromKey(key),Modifiers=((modifiers&ModifierKeys.Control)!=0?2:0)|((modifiers&ModifierKeys.Alt)!=0?1:0)|((modifiers&ModifierKeys.Shift)!=0?4:0)|((modifiers&ModifierKeys.Windows)!=0?8:0)};}
    }
    public sealed class ShortcutSet {
        public Shortcut Capture=new Shortcut {Key=0x70};
        public Shortcut Pin=new Shortcut {Key=0x72};
        public Shortcut Translate=new Shortcut {Key=0x54,Modifiers=3};
        public Shortcut[] Values(){return new[]{Capture,Pin,Translate};}
        public ShortcutSet Copy(){return new ShortcutSet {Capture=Capture.Copy(),Pin=Pin.Copy(),Translate=Translate.Copy()};}
        public string Error(){var values=Values();if(values.Any(v=>v==null||!v.Valid()))return "请使用 F1–F24，或 Ctrl / Alt 加其他键（不使用 Win / Esc）。";if(values.Select(v=>v.ToString()).Distinct().Count()!=3)return "三个功能不能使用相同的快捷键。";return null;}
    }
    // Reuse owned registrations, reserve new ones before committing, never release
    // working keys on failure. No keyboard hook or polling loop.
    public sealed class HotkeyManager : IDisposable {
        sealed class Entry {public Shortcut Key;public int Action;}
        readonly Func<int,Shortcut,bool> register;
        readonly Action<int> unregister;
        Dictionary<int,Entry> active=new Dictionary<int,Entry>();
        int nextId=100;
        public HotkeyManager(IntPtr window):this((id,key)=>Native.RegisterHotKey(window,id,(uint)(key.Modifiers|0x4000),(uint)key.Key),id=>Native.UnregisterHotKey(window,id)){}
        internal HotkeyManager(Func<int,Shortcut,bool> add,Action<int> remove){register=add;unregister=remove;}
        int NewId(){for(int attempts=0;attempts<0xBFFF;attempts++){nextId=nextId>=0xBFFF?100:nextId+1;if(!active.ContainsKey(nextId))return nextId;}throw new InvalidOperationException("快捷键注册失败。");}
        public int ActionFor(int id){Entry entry;return active.TryGetValue(id,out entry)?entry.Action:-1;}
        public Shortcut KeyFor(int id){Entry entry;return active.TryGetValue(id,out entry)?entry.Key.Copy():null;}
        public string Start(ShortcutSet keys){var failures=new List<string>();var values=keys.Values();for(int i=0;i<values.Length;i++){int id=NewId();if(register(id,values[i]))active[id]=new Entry {Key=values[i].Copy(),Action=i};else failures.Add(values[i].ToString());}return failures.Count==0?null:string.Join("、",failures)+" 已被占用，请在设置中更换；菜单仍可使用。";}
        public string Apply(ShortcutSet keys,Action save){
            string error=keys.Error();if(error!=null)return error;var desired=new Dictionary<int,Entry>();var added=new List<int>();
            try{var values=keys.Values();for(int i=0;i<values.Length;i++){int id=active.Where(p=>p.Value.Key.Same(values[i])).Select(p=>p.Key).FirstOrDefault();if(id==0){id=NewId();if(!register(id,values[i]))throw new InvalidOperationException(values[i]+" 已被其他软件占用，原快捷键未改变。");added.Add(id);}desired[id]=new Entry {Key=values[i].Copy(),Action=i};}save();}
            catch(Exception ex){foreach(int id in added)unregister(id);return ex.Message;}
            var previous=active;active=desired;foreach(int id in previous.Keys)if(!desired.ContainsKey(id))unregister(id);return null;
        }
        public void Dispose(){foreach(int id in active.Keys)unregister(id);active.Clear();}
    }
    public sealed class ShortcutEditor : Button {
        public Shortcut Value {get;private set;}
        readonly Action<Shortcut> recorder;
        public ShortcutEditor(Shortcut value){
            Value=value.Copy();Content=Value.ToString();MinWidth=160;Padding=new Thickness(10,7,10,7);Margin=new Thickness(0,4,0,4);FontSize=13;
            recorder=Record;ToolTip="点击后按下新的快捷键，Esc 取消输入";
            Click+=(s,e)=>{Focus();AppState.ShortcutRecorder=recorder;Content="请按快捷键…";};
            GotKeyboardFocus+=(s,e)=>AppState.ShortcutRecorder=recorder;
            LostKeyboardFocus+=(s,e)=>{if(AppState.ShortcutRecorder==recorder)AppState.ShortcutRecorder=null;Content=Value.ToString();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Tab)return;e.Handled=true;if(e.Key==System.Windows.Input.Key.Escape){Content=Value.ToString();Keyboard.ClearFocus();if(AppState.ShortcutRecorder==recorder)AppState.ShortcutRecorder=null;return;}var key=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;Record(Shortcut.FromKey(key,Keyboard.Modifiers));};
        }
        void Record(Shortcut key){if(key==null)return;if(!key.Valid()){Content="请加 Ctrl / Alt";return;}Value=key.Copy();Content=Value.ToString();}
        public void Reset(Shortcut key){Value=key.Copy();Content=Value.ToString();}
    }
}
