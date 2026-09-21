$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version='7.6.6'
$expected='02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860'
$archive=Join-Path $root '.cache/PowerShell-7.6.6-win-x64.zip'
New-Item -ItemType Directory -Force -Path (Join-Path $root '.cache') | Out-Null
if (!(Test-Path -LiteralPath $archive)) {Invoke-WebRequest "https://github.com/PowerShell/PowerShell/releases/download/v$version/PowerShell-$version-win-x64.zip" -OutFile $archive}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {throw 'PowerShell archive hash mismatch.'}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'runtimes/powershell') -Force
Write-Output "Verified and extracted PowerShell $version."
