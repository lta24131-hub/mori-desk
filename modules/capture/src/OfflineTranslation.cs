using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingJie {
    public static class OfflineTranslation {
        internal static bool Supported(string text){return text.All(c=>!char.IsLetter(c)||(c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='\u3400'&&c<='\u9fff'));}
        public static async Task<string[]> Translate(IList<string> texts,string target,CancellationToken cancel){
            Translation.TargetLanguage(target);if(texts.Any(t=>!Supported(t)))throw new InvalidOperationException("离线包仅支持中文和英文。其他语言请手动切换在线服务；此次没有联网发送文字。");
            if(texts.Sum(t=>t.Length)>50000)throw new InvalidOperationException("离线翻译文字过多，请分区域翻译。");
            await OfflinePacks.Gate.WaitAsync(cancel);try{
                var installed=OfflinePacks.Installed;if(installed==null)throw new InvalidOperationException("请在设置中下载中英离线包。当前为离线模式，不会改用联网翻译。");
                return await Task.Run(()=>RunProcess(OfflinePacks.PackPath(installed.Catalog),texts,target,cancel),cancel);
            }finally{OfflinePacks.Gate.Release();}
        }
        static void Kill(Process process){try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
        internal static string[] RunProcess(string root,IList<string> texts,string target,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            var start=new ProcessStartInfo(Path.Combine(root,"runtime","python.exe"),"-I -B \""+Path.Combine(root,"worker.py")+"\" \""+root+"\"") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=root,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                using(var process=Process.Start(start))using(timeout.Token.Register(()=>Kill(process))){
                    try{
                        // Drain both pipes concurrently; source text never goes on the command
                        // line, disk, or network. Closing stdin terminates the single request.
                        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                        byte[] request=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(new {texts=texts,target=target}));process.StandardInput.BaseStream.Write(request,0,request.Length);process.StandardInput.Close();
                        process.WaitForExit();timeout.Token.ThrowIfCancellationRequested();string raw=output.GetAwaiter().GetResult();errors.GetAwaiter().GetResult();
                        if(process.ExitCode!=0||raw.Length>400000)throw new InvalidOperationException("离线翻译失败，请缩短文字后重试，或在设置中重新下载离线包。没有向网上发送文字。");
                        var result=new JavaScriptSerializer().Deserialize<Dictionary<string,string[]>>(raw);string[] translated;if(result==null||!result.TryGetValue("translations",out translated)||translated==null||translated.Length!=texts.Count||translated.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("离线译文不完整，原图和原文已保留。");return translated;
                    }catch(Exception){cancel.ThrowIfCancellationRequested();if(timeout.IsCancellationRequested)throw new TimeoutException("离线翻译超时，请缩短文字后重试。此次没有联网。");throw;}
                    finally{Kill(process);try{process.WaitForExit(3000);}catch(InvalidOperationException){}}
                }
            }
        }
    }
}
