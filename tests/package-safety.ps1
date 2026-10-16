$ErrorActionPreference='Stop'
$suite=Split-Path $PSScriptRoot -Parent
$tokens=$null;$errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $suite 'package.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Package script did not parse.'}
$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-NoMachineSecrets'},$true)
if(!$function){throw 'Package secret guard is missing.'}
Invoke-Expression $function.Extent.Text
$fixture=Join-Path ([System.IO.Path]::GetTempPath()) ('Mori-package-guard-'+[guid]::NewGuid().ToString('N'))
$extension=Join-Path $fixture 'browser-extension'
New-Item -ItemType Directory -Path $extension | Out-Null
Assert-NoMachineSecrets $fixture
foreach($name in @('local-connection.js','local-connection.js.123.tmp','local-key.txt','local-key.txt.123.tmp','github-token.dat','pairing.json')){
 $path=Join-Path $extension $name
 [System.IO.File]::WriteAllText($path,'synthetic fixture; no actual pairing key')
 $rejected=$false
 try{Assert-NoMachineSecrets $fixture}catch{$rejected=$true}
 if(!$rejected){throw ('Secret guard failed: '+$name)}
 # Delete only the fixture file just created; never a real pairing file.
 Remove-Item -LiteralPath $path
}
Assert-NoMachineSecrets $fixture
Write-Output 'PASS: clean payload allowed; pairing files and interrupted-write remnants denied.'
