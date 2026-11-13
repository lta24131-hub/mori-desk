using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
// VM-only read-only CoreAudio inventory. It never activates a stream or changes devices/volume.
internal static class AudioEndpointGuest {
 [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]class MMDeviceEnumerator {}
 [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMMDeviceEnumerator {
  [PreserveSig]int EnumAudioEndpoints(int flow,uint mask,out IMMDeviceCollection devices);
  [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice endpoint);
  [PreserveSig]int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice device);
  [PreserveSig]int RegisterEndpointNotificationCallback(IntPtr callback);
  [PreserveSig]int UnregisterEndpointNotificationCallback(IntPtr callback);
 }
 [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMMDeviceCollection {
  [PreserveSig]int GetCount(out uint count);
  [PreserveSig]int Item(uint index,out IMMDevice device);
 }
 [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IMMDevice {
  [PreserveSig]int Activate(ref Guid iid,uint clsctx,IntPtr activation,out IntPtr value);
  [PreserveSig]int OpenPropertyStore(uint mode,out IntPtr properties);
  [PreserveSig]int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
  [PreserveSig]int GetState(out uint state);
 }
 static void HR(int result){if(result<0)Marshal.ThrowExceptionForHR(result);}
 static void Release(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
 static string Hex(int result){return "0x"+unchecked((uint)result).ToString("X8");}
 static object Device(IMMDevice device,out uint state){string id;HR(device.GetId(out id));HR(device.GetState(out state));return new{id,state,active=state==1};}
 static void Guard(){bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject item in query.Get())using(item)vm|=Regex.IsMatch(Convert.ToString(item["Manufacturer"])+" "+Convert.ToString(item["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);if(!vm||!Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Dedicated NookTester QEMU VM required");string path=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');if(!Regex.IsMatch(path,@"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"))throw new IOException("Hash-addressed audit package required");for(string p=path;!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse point refused");}
 [STAThread]static int Main(string[] args){try{Guard();if(args.Length!=1||args[0]!="--read-render-endpoints")throw new ArgumentException("Explicit read-only command required");var report=new Dictionary<string,object>();report["scope"]="VM-only read-only endpoint inventory; no audio stream or volume/device changes, no product execution";report["utc"]=DateTime.UtcNow.ToString("O");var services=new List<object>();int serviceRunning=0;using(var query=new ManagementObjectSearcher("SELECT Name,State,StartMode FROM Win32_Service WHERE Name='Audiosrv' OR Name='AudioEndpointBuilder'"))foreach(ManagementObject item in query.Get())using(item){services.Add(new{name=Convert.ToString(item["Name"]),state=Convert.ToString(item["State"]),startMode=Convert.ToString(item["StartMode"])});if(Convert.ToString(item["State"])=="Running")serviceRunning++;}report["services"]=services;var hardware=new List<object>();using(var query=new ManagementObjectSearcher("SELECT Name,Status,ConfigManagerErrorCode,PNPDeviceID FROM Win32_SoundDevice"))foreach(ManagementObject item in query.Get())using(item)hardware.Add(new{name=Convert.ToString(item["Name"]),status=Convert.ToString(item["Status"]),errorCode=Convert.ToString(item["ConfigManagerErrorCode"]),pnpDeviceId=Convert.ToString(item["PNPDeviceID"])});report["hardware"]=hardware;
   IMMDeviceEnumerator enumerator=null;IMMDeviceCollection collection=null;int active=0,defaults=0;var devices=new List<object>();var roles=new List<object>();try{enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator();HR(enumerator.EnumAudioEndpoints(0,15,out collection));uint count;HR(collection.GetCount(out count));if(count>128)throw new InvalidDataException("Unexpected endpoint count");for(uint i=0;i<count;i++){IMMDevice device=null;try{HR(collection.Item(i,out device));uint state;devices.Add(Device(device,out state));if(state==1)active++;}finally{Release(device);}}for(int role=0;role<3;role++){IMMDevice device=null;try{int result=enumerator.GetDefaultAudioEndpoint(0,role,out device);uint state=0;object detail=result>=0?Device(device,out state):null;roles.Add(new{role,roleName=new[]{"Console","Multimedia","Communications"}[role],hresult=Hex(result),device=detail});if(result>=0&&state==1)defaults++;}finally{Release(device);}}}finally{Release(collection);Release(enumerator);}report["renderEndpoints"]=devices;report["defaultRenderEndpoints"]=roles;report["activeRenderCount"]=active;report["activeDefaultRoles"]=defaults;bool ready=active>0&&defaults==3&&serviceRunning==2;report["environmentReady"]=ready;report["productPassed"]=null;string json=new JavaScriptSerializer{MaxJsonLength=262144}.Serialize(report);string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"audio-render-endpoints.json");using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(json);writer.Flush();stream.Flush(true);}Console.WriteLine("OBSERVED CoreAudio render environment ready="+ready+"; active="+active+"; defaultRoles="+defaults+"; runningServices="+serviceRunning);return ready?0:1;
  }catch(Exception error){Console.WriteLine(error);return 1;}}
}
