param([Parameter(Mandatory=$true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId)
$ErrorActionPreference='Stop'
$suiteExe=Join-Path $PSScriptRoot 'Mori.exe'
if(-not(Test-Path -LiteralPath $suiteExe)){$suiteExe=Join-Path $PSScriptRoot 'dist\Mori.exe'}
if(-not(Test-Path -LiteralPath $suiteExe)){throw 'Mori Desk executable not found'}
$suiteConnection=Join-Path $env:LOCALAPPDATA 'MoriDesk\browser'
New-Item -ItemType Directory -Force $suiteConnection | Out-Null
$manifestPath=Join-Path $suiteConnection 'app.mori.desk.json'
$manifest=@{name='app.mori.desk';description='Mori Desk local library';path=$suiteExe;type='stdio';allowed_origins=@("chrome-extension://$ExtensionId/")} | ConvertTo-Json
[System.IO.File]::WriteAllText($manifestPath,$manifest,(New-Object System.Text.UTF8Encoding($false)))
foreach($browser in @('Google\Chrome','Microsoft\Edge')){
 $suiteKey="HKCU:\Software\$browser\NativeMessagingHosts\app.mori.desk"
 New-Item -Path $suiteKey -Force | Out-Null
 Set-Item -LiteralPath $suiteKey -Value $manifestPath
}
Write-Output 'Mori Desk connected. Existing Mori registration is unchanged.'
