# Build-time compatibility barrier, not a migration engine or a runtime backup restore.
# There is intentionally no refresh, accept-changes, or skip switch.
$script:NookDataContractDefault = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\update-data-contract.json'
if (-not ('NookPackaging.StrictContractJson' -as [type])) {
 Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
namespace NookPackaging {
 public sealed class StrictContractJson {
  readonly string text; int at;
  StrictContractJson(string value) { text=value; }
  static Exception Bad() { return new InvalidDataException("Invalid or ambiguous update data contract JSON."); }
  void Space() { while(at<text.Length && (text[at]==' ' || text[at]=='\t' || text[at]=='\n' || text[at]=='\r')) at++; }
  bool Take(char c) { Space(); if(at<text.Length && text[at]==c) {at++; return true;} return false; }
  void Need(char c) { if(!Take(c)) throw Bad(); }
  string String() {
   Need('"'); var value=new StringBuilder(); bool closed=false;
   while(at<text.Length) {
    char c=text[at++]; if(c=='"') {closed=true;break;} if(c<32) throw Bad();
    if(c=='\\') {
     if(at==text.Length) throw Bad(); c=text[at++];
     switch(c) {
      case '"': case '\\': case '/': break;
      case 'b': c='\b';break; case 'f':c='\f';break; case 'n':c='\n';break; case 'r':c='\r';break; case 't':c='\t';break;
      case 'u':
       if(at+4>text.Length) throw Bad(); int code;
       if(!int.TryParse(text.Substring(at,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out code)) throw Bad();
       c=(char)code;at+=4;break;
      default: throw Bad();
     }
    }
    value.Append(c);
   }
   if(!closed) throw Bad();
   string result=value.ToString();
   for(int i=0;i<result.Length;i++) if(char.IsSurrogate(result[i])) {
    if(!char.IsHighSurrogate(result[i]) || i+1==result.Length || !char.IsLowSurrogate(result[++i])) throw Bad();
   }
   return result;
  }
  object Value(int depth) {
   if(depth>6) throw Bad(); Space(); if(at==text.Length) throw Bad();
   if(text[at]=='"') return String();
   if(Take('{')) {
    var map=new Dictionary<string,object>(StringComparer.Ordinal); if(Take('}')) return map;
    do { string key=String(); Need(':'); if(map.ContainsKey(key)) throw Bad(); map.Add(key,Value(depth+1)); if(map.Count>1024) throw Bad(); } while(Take(','));
    Need('}');return map;
   }
   if(Take('[')) {
    var list=new List<object>();if(Take(']'))return list.ToArray();
    do {list.Add(Value(depth+1));if(list.Count>512)throw Bad();} while(Take(','));Need(']');return list.ToArray();
   }
   // This contract has only one numeric field, an unsigned integral schema.
   int start=at;while(at<text.Length && text[at]>='0' && text[at]<='9')at++;
   long number;
   if(at==start || (at-start>1 && text[start]=='0') || !long.TryParse(text.Substring(start,at-start),NumberStyles.None,CultureInfo.InvariantCulture,out number))throw Bad();
   return number;
  }
  public static Dictionary<string,object> Parse(string text) {
   if(text==null || text.Length==0 || text.Length>131072)throw Bad();
   var parser=new StrictContractJson(text);var result=parser.Value(0) as Dictionary<string,object>;
   parser.Space();if(result==null || parser.at!=text.Length)throw Bad();return result;
  }
 }
}
'@
}

function Assert-UpdateDataContract {
 [CmdletBinding()]
 param([Parameter(Mandatory=$true)][string]$SourceRoot,[string]$ManifestPath=$script:NookDataContractDefault)
 $ErrorActionPreference='Stop'
 function Plain([string]$Path,[bool]$Directory) {
  if($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.IndexOf(':',2) -ge 0){throw 'Data-contract inputs must use absolute local physical paths.'}
  $full=[IO.Path]::GetFullPath($Path)
  $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
  if($item.PSIsContainer -ne $Directory){throw 'Unexpected data-contract input type.'}
  for($part=$full;$part;$part=[IO.Path]::GetDirectoryName($part)) {
   if(([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Data-contract paths cannot contain links or redirected directories.'}
  }
  return $full
 }
 function Shape($Value,[string[]]$Names) {
  if($Value -isnot [Collections.Generic.Dictionary[string,object]] -or $Value.Count -ne $Names.Count){throw 'Unexpected data-contract record shape.'}
  foreach($name in $Names){if(-not $Value.ContainsKey($name)){throw ('Missing data-contract field: '+$name)}}
 }
 function Hash([string]$File) {
  $input=[IO.File]::Open($File,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
  $algorithm=[Security.Cryptography.SHA256]::Create()
  try{return [BitConverter]::ToString($algorithm.ComputeHash($input)).Replace('-','').ToLowerInvariant()}
  finally{$algorithm.Dispose();$input.Dispose()}
 }
 $root=(Plain $SourceRoot $true).TrimEnd('\','/')
 $manifest=Plain $ManifestPath $false
 $input=[IO.File]::Open($manifest,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {
  if($input.Length -lt 1 -or $input.Length -gt 131072){throw 'Update data contract exceeds its bounded size.'}
  $bytes=New-Object byte[] ([int]$input.Length);$offset=0
  while($offset -lt $bytes.Length){$read=$input.Read($bytes,$offset,$bytes.Length-$offset);if($read -eq 0){throw 'Incomplete data contract.'};$offset+=$read}
 } finally {$input.Dispose()}
 $text=([Text.UTF8Encoding]::new($false,$true)).GetString($bytes)
 $record=[NookPackaging.StrictContractJson]::Parse($text)
 Shape $record @('schemaVersion','contractId','baselineVersion','rollbackPolicy','reviewPolicy','files')
 if($record['schemaVersion'] -isnot [long] -or $record['schemaVersion'] -ne 1 -or
    $record['contractId'] -isnot [string] -or $record['contractId'] -cnotmatch '^nook-[a-z0-9-]{1,64}$' -or
    $record['baselineVersion'] -isnot [string] -or $record['baselineVersion'] -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    $record['rollbackPolicy'] -cne 'same-reviewed-data-contract-only' -or
    $record['reviewPolicy'] -cne 'manual-review-and-vm-rollback-evidence-required' -or
    $record['files'] -isnot [object[]] -or $record['files'].Length -lt 1){throw 'Unsupported update data contract.'}
 $required=@(
  'modules/library/src/Library.cs','modules/library/src/Database.cs','modules/library/src/Library.BookmarkFolders.cs',
  'modules/library/src/Library.Deletions.cs','src/Library.ReferenceAssets.cs','src/BoardStore.cs','src/ReferenceStore.cs',
  'modules/library/src/UndoHistory.cs','src/Library.LocalCollections.cs','src/Library.OverviewVisibility.cs','src/Library.FileConversion.cs',
  'modules/library/src/Program.cs','modules/library/src/Settings.Persistence.cs','modules/library/src/Settings.Storage.cs',
  'modules/library/src/Settings.Customization.cs','modules/capture/src/Core.cs','modules/capture/src/PinStore.cs',
  'src/FreeCanvasStore.cs','src/MatureCanvasStore.cs','modules/library/src/Library.ThumbnailRecovery.cs',
  'modules/library/src/Library.Restore.cs','modules/library/src/LibraryTransfer.cs','modules/library/src/ThumbnailImage.cs',
  'modules/library/src/Library.Batch.cs','modules/library/src/Library.Bookmarks.cs','modules/library/src/Library.FolderBatch.cs'
 )
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 $mismatches=[Collections.Generic.List[string]]::new()
 foreach($pin in $record['files']) {
  Shape $pin @('path','sha256')
  $relative=$pin['path'];$digest=$pin['sha256']
  if($relative -isnot [string] -or $relative -cnotmatch '^(src|modules/(library|capture)/src)/[A-Za-z][A-Za-z0-9.]*\.cs$' -or
     $relative.Contains('..') -or $digest -isnot [string] -or $digest -cnotmatch '^[a-f0-9]{64}$' -or -not $seen.Add($relative)){throw 'Unsafe, duplicate, or invalid data-contract file pin.'}
  if([IO.Path]::GetFileName($relative).Split('.')[0] -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'){throw 'Reserved device name in update data contract.'}
  $file=Plain (Join-Path $root $relative) $false
  if(-not $file.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Data-contract file escaped the source root.'}
  if((Hash $file) -cne $digest){$mismatches.Add($relative)}
 }
 foreach($relative in $required){if(-not $seen.Contains($relative)){throw ('Required compatibility pin removed: '+$relative)}}
 # Newly added partial stores cannot silently bypass the existing contract.
 foreach($scope in @('modules/library/src','src')) {
  $directory=Plain (Join-Path $root $scope) $true
  foreach($file in Get-ChildItem -LiteralPath $directory -File -Force) {
   if($file.Name -like 'Library.*.cs' -or $file.Name -like 'Settings.*.cs' -or $file.Name -like '*CanvasStore.cs') {
    if(-not $seen.Contains($scope+'/'+$file.Name)){throw ('Unreviewed persistence source added: '+$scope+'/'+$file.Name)}
   }
  }
 }
 if($mismatches.Count){throw ('Data compatibility changed; packaging stopped. Do not auto-refresh hashes. Review migrations and supply VM rollback evidence before revising the contract. Changed: '+($mismatches -join ', '))}
 $algorithm=[Security.Cryptography.SHA256]::Create()
 try{$digest=[BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()}finally{$algorithm.Dispose()}
 if((Hash $manifest) -cne $digest){throw 'Update data contract changed during validation.'}
 return [pscustomobject]@{ContractId=$record['contractId'];BaselineVersion=$record['baselineVersion'];ManifestSha256=$digest;SourceRoot=$root;Files=$seen.Count;AutomaticMetadataRestore=$false}
}
