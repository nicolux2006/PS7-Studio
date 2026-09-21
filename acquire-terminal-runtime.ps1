[CmdletBinding()]
param([switch]$Offline, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cache = Join-Path $root '.cache/terminal'
$vendor = Join-Path $root 'src/PS7Studio.App/Assets/Terminal/vendor'
$pses = Join-Path $root 'runtimes/editorservices'
$packages = @(
    @{ File='PowerShellEditorServices-4.7.0.zip'; Url='https://github.com/PowerShell/PowerShellEditorServices/releases/download/v4.7.0/PowerShellEditorServices.zip'; Hash='5084F0326CC88539E9D880B08F6C52CE5BE4672BB8ECEE8921E35F8895A6B9D7' },
    @{ File='xterm-6.0.0.tgz'; Url='https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz'; Hash='908E66E04AF6C8DC6B00DD3B54DE088E2E81E5ED866284FD6C2FB3C2D1C7A3F6' },
    @{ File='addon-fit-0.11.0.tgz'; Url='https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz'; Hash='26003B4517A132B64E4FF228FD88A5FDA3FFF5E606C76093F6DCFF772E9ECEC0' }
)
if (-not $VerifyOnly) { New-Item -ItemType Directory -Force $cache,$vendor,$pses | Out-Null }
foreach ($package in $packages) {
    $archive = Join-Path $cache $package.File
    if (-not (Test-Path -LiteralPath $archive)) {
        if ($Offline -or $VerifyOnly) { throw "Missing cached archive: $archive" }
        Invoke-WebRequest -Uri $package.Url -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) { throw "SHA256 mismatch: $archive" }
}
if (-not $VerifyOnly) {
    Expand-Archive -LiteralPath (Join-Path $cache $packages[0].File) -DestinationPath $pses -Force
    # Extract only distribution files and licenses, without sources, maps, or npm dependencies.
    $temp = Join-Path $cache 'vendor-extract'
    New-Item -ItemType Directory -Force $temp | Out-Null
    try {
        foreach ($spec in @(
            @{ Archive=$packages[1].File; Entries=@('package/lib/xterm.js','package/css/xterm.css','package/LICENSE'); Outputs=@('xterm.js','xterm.css','LICENSE.xterm') },
            @{ Archive=$packages[2].File; Entries=@('package/lib/addon-fit.js','package/LICENSE'); Outputs=@('addon-fit.js','LICENSE.addon-fit') }
        )) {
            & tar.exe -xzf (Join-Path $cache $spec.Archive) -C $temp @($spec.Entries)
            if ($LASTEXITCODE -ne 0) { throw "npm archive extraction failed: $($spec.Archive)" }
            for ($i=0; $i -lt $spec.Entries.Count; $i++) { Copy-Item -LiteralPath (Join-Path $temp $spec.Entries[$i]) -Destination (Join-Path $vendor $spec.Outputs[$i]) -Force }
        }
    } finally {
        $resolvedTemp = [IO.Path]::GetFullPath($temp)
        if (-not $resolvedTemp.StartsWith([IO.Path]::GetFullPath($cache) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid temporary path.' }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
foreach ($relative in @('PowerShellEditorServices/Start-EditorServices.ps1','PowerShellEditorServices/PowerShellEditorServices.psd1','LICENSE','NOTICE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pses $relative) -PathType Leaf)) { throw "Incomplete PSES extraction: $relative" }
}
$assetHashes = @{
    'xterm.js'='14903579FF54664CD72F8E8699E6961A6272C21863EC1C3B118CDC8AF5D4A972'
    'xterm.css'='854A7C0FB70E8B1A083C16797AB827299FB18744F5AD34F227B48337E33293C6'
    'addon-fit.js'='BA3EA256CE0620A0992A197D6C9BAEA64823FC93D8DA07A9E366CA9943C18527'
    'LICENSE.xterm'='B569F629D00F2626A8100DF2A1798210535621E42164DFD426A6FE5AAC7B0CCD'
    'LICENSE.addon-fit'='E256F01188AF527E4D06D21D06FBF785AE9C50D4B328BF03CBE0BA7F0AA4228F'
}
foreach ($name in $assetHashes.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $vendor $name) -PathType Leaf)) { throw "Missing terminal asset: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $vendor $name)).Hash -ne $assetHashes[$name]) { throw "Terminal asset hash mismatch: $name" }
}
Write-Output 'Verified pinned archives and terminal runtime layout: PSES 4.7.0; external Evergreen WebView2 prerequisite; xterm 6.0.0; addon-fit 0.11.0.'

