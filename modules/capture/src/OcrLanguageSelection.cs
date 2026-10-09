using System;
using System.Linq;

namespace QingJie {
    // Optional languages are independent; Chinese/English are never in this set.
    internal sealed class OcrLanguageSelection {
        readonly Preferences settings;
        readonly Action save;
        readonly Func<string,bool> installed;
        readonly Action<string> remove;
        public string[] CurrentIds {get{return OcrLanguagePacks.ParseSelection(settings.OcrLanguage);}}
        public string[] EffectiveIds {get{return CurrentIds.Where(installed).ToArray();}}
        public OcrLanguageSelection(Preferences settings,Action save,Func<string,bool> installed,Action<string> remove){this.settings=settings;this.save=save;this.installed=installed;this.remove=remove;}
        public bool Installed(string id){return installed(id);}
        public bool Enabled(string id){return CurrentIds.Contains(id);}
        public void SetEnabled(string id,bool enabled){
            if(OcrLanguagePacks.Find(id)==null)throw new ArgumentException("Unknown OCR language");
            if(enabled&&!installed(id))throw new InvalidOperationException("请先下载该识别语言包。");
            var ids=CurrentIds.ToList();if(enabled){if(!ids.Contains(id))ids.Add(id);}else ids.Remove(id);
            Commit(string.Join("+",ids));
        }
        void Commit(string value){
            string next=OcrLanguagePacks.NormalizeSelection(value),before=settings.OcrLanguage;if(before==next)return;
            settings.OcrLanguage=next;try{save();}catch{settings.OcrLanguage=before;throw;}
        }
        public void Remove(string id){
            if(OcrLanguagePacks.Find(id)==null)throw new ArgumentException("Unknown OCR language");
            string before=settings.OcrLanguage;bool active=Enabled(id);
            if(active)SetEnabled(id,false);
            try{if(installed(id))remove(id);}catch{if(active&&installed(id))Commit(before);throw;}
        }
    }
}
