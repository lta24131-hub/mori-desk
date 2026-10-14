using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace MoriDesk {
 internal static class EmbeddedThemeTests {
  static readonly string[] Keys={"Canvas","Panel","Surface","Input","Control","Border","Divider","Text","Muted","Hover","Accent","AccentSoft","ScrollThumb","Primary","OnPrimary"};
  static readonly string[] Tokens={"canvas","panel","surface","input","control","border","divider","text","muted","hover","accent","accent-soft","scroll-thumb","primary","on-primary"};
  static readonly string[] BrowserTokens={"bg","panel","surface","input","control","line","divider","text","muted","hover","accent","active","scroll-thumb","primary","on-primary"};
  static readonly int[] NeutralRoles={0,1,2,3,4,5,6,7,8,9,12,14};
  static readonly string[][] Palettes={
   new[]{"#FAFAFA","#F3F3F3","#FFFFFF","#FAFAFA","#EEEEEE","#D7D7D7","#E4E4E4","#252525","#626262","#E7E7E7","#6D5BC7","#F0EDFA","#B7B7B7","#6D5BC7","#FFFFFF"},
   new[]{"#161616","#1D1D1D","#242424","#292929","#303030","#484848","#363636","#EEEEEE","#AAAAAA","#3B3B3B","#B7A8ED","#322D40","#626262","#B7A8ED","#161616"}
  };
  static string Rule(string css,string selector){
   var templates=Regex.Matches(css,@"(?:style\.textContent\s*=\s*|const css\s*=\s*)`([\s\S]*?)`;");
   if(templates.Count>0)css=String.Join("\n",templates.Cast<Match>().Select(match=>match.Groups[1].Value).ToArray());
   css=Regex.Replace(css,@"/\*[\s\S]*?\*/","");
   return String.Join(";",Regex.Matches(css,@"([^{}]+)\{([^{}]*)\}").Cast<Match>().Where(match=>match.Groups[1].Value.Split(',').Any(part=>part.Trim()==selector)).Select(match=>match.Groups[2].Value).ToArray());
  }
  static string Declaration(string body,string property){var matches=Regex.Matches(body,@"(?:^|;)\s*"+Regex.Escape(property)+@"\s*:\s*([^;]+)");return matches.Count==0?"":matches[matches.Count-1].Groups[1].Value.Trim();}
  static void CheckTokens(string css,string selector,Action<bool,string> check,params string[] expected){
   string body=Rule(css,selector);check(body.Length>0,"bundled theme includes "+selector);
   foreach(string entry in expected){string[] pair=entry.Split('|');check(Declaration(body,pair[0])==pair[1],selector+" "+pair[0]+" uses "+pair[1]);}
  }
  static void CheckNeutral(string hex,string description,Action<bool,string> check){
   bool valid=Regex.IsMatch(hex,@"^#[0-9A-F]{6}(?:[0-9A-F]{2})?$",RegexOptions.IgnoreCase);
   check(valid&&hex.Substring(1,2).Equals(hex.Substring(3,2),StringComparison.OrdinalIgnoreCase)&&hex.Substring(3,2).Equals(hex.Substring(5,2),StringComparison.OrdinalIgnoreCase),description+" has equal red, green and blue channels");
  }
  internal static void Run(Action<bool,string> check){
   string css=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"web","model","desktop-theme.css"));
   string popup=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"browser-extension","popup.css"));
   string content=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"browser-extension","content.js"));
   string previous=Mori.Theme.Current;
   try{
    foreach(string theme in new[]{"light","dark"}){
     Mori.Theme.Set(theme);
     string[] palette=Palettes[theme=="light"?0:1];
     string modelRule=Rule(css,"html[data-nook-theme=\""+theme+"\"]");
     string popupRule=Rule(popup,theme=="light"?":root":":root[data-theme=\"dark\"]");
     string collectorRule=Rule(content,theme=="light"?".panel":".panel[data-theme=\"dark\"]");
     check(modelRule.Contains("color-scheme: "+theme),theme+" embedded model UI sets native input color scheme");
     for(int i=0;i<Keys.Length;i++){
      var color=((SolidColorBrush)Mori.Theme.Brush(Keys[i])).Color;string hex="#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2");
      check(hex==palette[i],theme+" desktop "+Keys[i]+" matches the neutral/interaction palette");
      check(Declaration(modelRule,"--nook-"+Tokens[i]).Equals(hex,StringComparison.OrdinalIgnoreCase),theme+" model chrome token matches desktop "+Keys[i]);
      check(Declaration(popupRule,"--"+BrowserTokens[i]).Equals(hex,StringComparison.OrdinalIgnoreCase),theme+" popup token matches desktop "+Keys[i]);
      check(Declaration(collectorRule,"--"+BrowserTokens[i]).Equals(hex,StringComparison.OrdinalIgnoreCase),theme+" collector token matches desktop "+Keys[i]);
      if(NeutralRoles.Contains(i)){
       check(color.A==255&&color.R==color.G&&color.G==color.B,theme+" desktop ordinary "+Keys[i]+" is opaque neutral grey");
       CheckNeutral(Declaration(modelRule,"--nook-"+Tokens[i]),theme+" model "+Tokens[i],check);
       CheckNeutral(Declaration(popupRule,"--"+BrowserTokens[i]),theme+" popup "+BrowserTokens[i],check);
       CheckNeutral(Declaration(collectorRule,"--"+BrowserTokens[i]),theme+" collector "+BrowserTokens[i],check);
      }else check(color.B>color.R&&color.R>color.G,theme+" "+Keys[i]+" remains purple for explicit interactions");
     }
     string toastRule=Rule(content,theme=="light"?".toast":".toast[data-theme=\"dark\"]");
     foreach(string entry in new[]{"bg|"+palette[2],"text|"+palette[7],"line|"+palette[5],"accent|"+palette[10]}){string[] pair=entry.Split('|');check(Declaration(toastRule,"--"+pair[0]).Equals(pair[1],StringComparison.OrdinalIgnoreCase),theme+" toast "+pair[0]+" matches desktop palette");if(pair[0]!="accent")CheckNeutral(Declaration(toastRule,"--"+pair[0]),theme+" toast "+pair[0],check);}
     check(ModelWindow.ThemeDocumentScript(theme)=="document.documentElement.dataset.nookTheme='"+theme+"';",theme+" theme bridge only writes the dedicated UI attribute");
     string bootstrap=ModelWindow.ThemeBootstrapScript(theme,css);
     check(bootstrap.Contains(ModelWindow.ThemeDocumentScript(theme))&&bootstrap.Contains("nook-desktop-theme")&&bootstrap.Contains("observer.disconnect()"),theme+" initial document receives stylesheet once and retires bootstrap observer");
    }
    CheckOrdinaryChrome(css,popup,content,check);
    CheckInteractionChrome(css,popup,content,check);
    check(ModelWindow.ThemeDocumentScript("light';window.bad=true;//")==ModelWindow.ThemeDocumentScript("dark"),"theme bridge never accepts executable text in theme names");
    string probe="body::before{content:\"quotes\\ and \u2028 newline\n\";}";string script=ModelWindow.ThemeBootstrapScript("light",probe);
    int start=script.IndexOf("style.textContent=",StringComparison.Ordinal)+"style.textContent=".Length;int end=script.IndexOf(";document.head.append",start,StringComparison.Ordinal);
    check(new JavaScriptSerializer().Deserialize<string>(script.Substring(start,end-start))==probe,"theme CSS is JSON-encoded without escaping or executable interpolation loss");
    string withoutComments=Regex.Replace(css,@"/\*[\s\S]*?\*/","");
    check(!Regex.IsMatch(withoutComments,@"\.(?:app-shell|viewer-stage|canvas-host|technical-grid|material-ball|axis-badge|axis-letter)(?:[\s.:>{,]|$)"),"desktop stylesheet does not target renderer backgrounds, canvases, materials or axis colors");
    check(!Regex.IsMatch(Rule(css,"html[data-nook-theme] .color-swatch.selected"),@"(?:^|;)\s*(?:background(?:-image|-color)?|color|filter)\s*:"),"selected model swatch only gains a border/outline and keeps its actual color");
    check(Regex.IsMatch(Rule(css,"html[data-nook-theme] .color-swatch.selected"),@"box-shadow\s*:\s*0 0 0 2px var\(--nook-accent\),\s*inset 0 1px 1px #FFFFFF59",RegexOptions.IgnoreCase),"selected model color swatch halo overrides upstream cyan with theme purple and preserves inset lighting");
    check(css.Contains("button:focus-visible")&&css.Contains(".color-picker:focus-within"),"embedded viewer chrome retains keyboard focus indicators");
    check(popup.Contains("html,html body{width:376px;min-width:376px;max-width:376px}")&&popup.Contains("--on-primary:#FFFFFF")&&popup.Contains("--on-primary:#161616"),"popup retains fixed safe width and distinct neutral primary button foregrounds");
    check(popup.Contains("--error:#a03f34")&&popup.Contains("--error:#f2ab9f")&&content.Contains("--error:#9b463a")&&content.Contains("--error:#f2ab9f"),"browser error semantics remain red in both themes");
   }finally{Mori.Theme.Set(previous);}
  }
  static void CheckOrdinaryChrome(string css,string popup,string content,Action<bool,string> check){
   CheckTokens(popup,"body",check,"background|var(--bg)","color|var(--text)");
   CheckTokens(popup,"header",check,"background|var(--panel)","border-bottom|1px solid var(--divider)");
   CheckTokens(popup,".card",check,"background|var(--surface)","border|1px solid var(--line)");
   CheckTokens(popup,"select",check,"background|var(--input)","border|1px solid var(--line)");
   foreach(string selector in new[]{".icon",".rule button","#bookmark-review button"})CheckTokens(popup,selector,check,"background|var(--control)");
   CheckTokens(popup,".nav:hover",check,"color|var(--text)","background|var(--hover)");
   foreach(string selector in new[]{".update-row button",".update-ack","#message"})CheckTokens(popup,selector,check,"color|var(--text)");
   foreach(string selector in new[]{".icon:hover",".choices label:hover span",".rule button:hover","#bookmark-review button:not(:disabled):hover"})CheckTokens(popup,selector,check,"background|var(--hover)");
   CheckTokens(popup,"main",check,"scrollbar-color|var(--scroll-thumb) transparent");
   foreach(string selector in new[]{".layout-preview.floating:after",".layout-preview.dock:after"})CheckTokens(popup,selector,check,"border|1px solid var(--muted)","background|var(--control)");
   CheckTokens(popup,".theme-preview",check,"background|#FAFAFA");CheckTokens(popup,".theme-preview.dark",check,"background|#161616");
   CheckTokens(popup,".theme-preview.system",check,"background|linear-gradient(90deg,#FAFAFA 50%,#161616 50%)");
   foreach(string selector in new[]{".theme-preview:after",".theme-preview.dark:after"}){
    string background=Declaration(Rule(popup,selector),"background");var colors=Regex.Matches(background,@"#[0-9A-F]{6}(?:[0-9A-F]{2})?\b",RegexOptions.IgnoreCase);
    check(!Regex.IsMatch(background,@"var\(--(?:accent|active|primary)\)")&&(colors.Count>0||Regex.IsMatch(background,@"var\(--(?:muted|line|text)\)")),selector+" sample lines use neutral colours");
    foreach(Match color in colors)CheckNeutral(color.Value,selector+" sample line",check);
   }
   CheckTokens(content,".panel",check,"background|var(--bg)","color|var(--text)","border|1px solid var(--line)");
   foreach(string selector in new[]{".editor input",".editor select"})CheckTokens(content,selector,check,"background|var(--input)","color|var(--text)","border|1px solid var(--line)");
   CheckTokens(content,".choice",check,"background|var(--control)","color|var(--text)","border|1px solid var(--line)");
   foreach(string selector in new[]{".category-toggle:hover",".folder:hover",".folder.hover"})CheckTokens(content,selector,check,"color|var(--text)","background|var(--hover)");
   CheckTokens(content,".folder-retry",check,"color|var(--text)");CheckTokens(content,".collect-icon",check,"color|var(--muted)");
   CheckTokens(content,".panel[data-phase=\"success\"] .quick",check,"border-color|var(--line)","background|var(--surface)");
   foreach(string selector in new[]{".folder-retry:hover",".choice:hover"})CheckTokens(content,selector,check,"background|var(--hover)");
   foreach(string selector in new[]{".editor",".folder-list"})CheckTokens(content,selector,check,"scrollbar-color|var(--scroll-thumb) transparent");
   foreach(string selector in new[]{".brand-mark",".empty-icon",".cloud-dialog-icon",".header-open",".tool-rail button",".appearance-heading button",".color-picker",".material-grid button",".axis-presets button",".quarter-turns button",".reset-orientation",".stop-loading",".model-info button",".cloud-dialog-actions button",".fullscreen-close",".export-light-button"}){
    string scoped="html[data-nook-theme] "+selector;CheckTokens(css,scoped,check,"color|var(--nook-text)","background|var(--nook-control)","border-color|var(--nook-border)");
    check(!Regex.IsMatch(Rule(css,scoped),@"var\(--nook-(?:accent(?:-soft)?|primary)\)"),selector+" ordinary chrome has no purple fill or text");
   }
   CheckTokens(css,"html[data-nook-theme] .export-light-button > svg",check,"color|var(--nook-text)");
   foreach(string selector in new[]{".offline-pill",".gesture-hint"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"color|var(--nook-text)","background|var(--nook-panel)","border-color|var(--nook-border)");
   CheckTokens(css,"html[data-nook-theme] .offline-pill.preparing",check,"color|var(--nook-muted)");CheckTokens(css,"html[data-nook-theme] .gesture-hint i",check,"background|var(--nook-divider)");
   CheckTokens(css,"html[data-nook-theme] .loader",check,"border-color|var(--nook-border)","border-top-color|var(--nook-muted)");
   CheckTokens(css,"html[data-nook-theme] .loading-progress",check,"background|var(--nook-control)");CheckTokens(css,"html[data-nook-theme] .loading-progress > span",check,"background|var(--nook-muted)");
   foreach(string selector in new[]{".header-open:not(:disabled):hover",".tool-rail button:not(:disabled):not(.active):hover",".appearance-heading button:hover",".material-grid button:not(.selected):hover",".axis-presets button:not(.selected):hover",".quarter-turns button:hover",".reset-orientation:hover",".model-info button:hover",".stop-loading:hover",".fullscreen-close:hover",".export-light-button:hover",".cloud-dialog-actions button:not(.primary):hover"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"background|var(--nook-hover)");
   foreach(string selector in new[]{".appearance-panel",".orientation-panel",".cloud-dialog"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"scrollbar-color|var(--nook-scroll-thumb) transparent");
  }
  static void CheckInteractionChrome(string css,string popup,string content,Action<bool,string> check){
   CheckTokens(popup,".choices input:checked+span",check,"border-color|var(--accent)","background|var(--active)","color|var(--accent)");
   CheckTokens(popup,"#bookmark-review #bookmark-confirm",check,"background|var(--primary)","color|var(--on-primary)","border-color|var(--primary)");
   CheckTokens(popup,".toggle-row input:checked",check,"background|var(--primary)");CheckTokens(popup,".toggle-row input:checked:after",check,"background|var(--on-primary)");
   foreach(string selector in new[]{"button:focus-visible","select:focus-visible","input:focus-visible","summary:focus-visible",".choices input:focus-visible+span"})CheckTokens(popup,selector,check,"outline|2px solid var(--accent)");
   CheckTokens(content,".category-toggle[aria-expanded=\"true\"]",check,"color|var(--accent)","background|var(--active)");
   CheckTokens(content,".choice.primary",check,"background|var(--primary)","color|var(--on-primary)","border-color|var(--primary)");CheckTokens(content,".is-over",check,"border-color|var(--accent)");
   foreach(string selector in new[]{".category-toggle:focus-visible",".folder:focus-visible",".folder-retry:focus-visible",".choice:focus-visible",".toast-action:focus-visible",".toast-close:focus-visible"})CheckTokens(content,selector,check,"outline|2px solid var(--accent)");
   foreach(string selector in new[]{".tool-rail button.active",".material-grid button.selected",".axis-presets button.selected"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"background|var(--nook-accent-soft)","color|var(--nook-accent)","border-color|var(--nook-accent)");
   foreach(string selector in new[]{".open-button",".cloud-dialog-actions button.primary"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"background|var(--nook-primary)","color|var(--nook-on-primary)","border-color|var(--nook-primary)");
   foreach(string selector in new[]{"button:focus-visible","input:focus-visible",".color-picker:focus-within"})CheckTokens(css,"html[data-nook-theme] "+selector,check,"outline|2px solid var(--nook-accent)");
  }
 }
}
