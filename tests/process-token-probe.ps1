param([Parameter(Mandatory=$true)][int[]]$ProcessIds)
# Read-only diagnostic: identifies restricted launch contexts; changes no token,
# permissions, registry values, browser profile, or security configuration.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class MoriTokenProbe {
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr t);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr t,int c,IntPtr b,int length,out int needed);
 static IntPtr Info(IntPtr t,int c){int n;GetTokenInformation(t,c,IntPtr.Zero,0,out n);if(n==0)throw new System.ComponentModel.Win32Exception();IntPtr b=Marshal.AllocHGlobal(n);if(!GetTokenInformation(t,c,b,n,out n)){Marshal.FreeHGlobal(b);throw new System.ComponentModel.Win32Exception();}return b;}
 static string Sid(IntPtr sid){var s=new SecurityIdentifier(sid);try{return s.Translate(typeof(NTAccount)).Value;}catch{return s.Value;}}
 public static string Describe(int id){IntPtr p=OpenProcess(0x1000,false,id),t=IntPtr.Zero;if(p==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();try{if(!OpenProcessToken(p,8,out t))throw new System.ComponentModel.Win32Exception();string result="PID="+id;
  foreach(int c in new[]{18,20,21,29}){IntPtr b=Info(t,c);try{result+="; token_"+c+"="+Marshal.ReadInt32(b);}finally{Marshal.FreeHGlobal(b);}}
  foreach(int c in new[]{1,25}){IntPtr b=Info(t,c);try{result+="; "+(c==1?"user":"integrity")+"="+Sid(Marshal.ReadIntPtr(b));}finally{Marshal.FreeHGlobal(b);}}
  IntPtr groups=Info(t,11);try{int count=Marshal.ReadInt32(groups);result+="; restricted_count="+count;int offset=IntPtr.Size==8?8:4, stride=IntPtr.Size==8?16:8;for(int i=0;i<count;i++)result+="; restricted="+Sid(Marshal.ReadIntPtr(groups,offset+i*stride));}finally{Marshal.FreeHGlobal(groups);}return result;
 }finally{if(t!=IntPtr.Zero)CloseHandle(t);CloseHandle(p);}}
}
'@
foreach($processId in $ProcessIds){try{[MoriTokenProbe]::Describe($processId)}catch{Write-Output ('PID='+$processId+'; ERROR='+$_.Exception.Message)}}
[MoriTokenProbe]::Describe($PID)
