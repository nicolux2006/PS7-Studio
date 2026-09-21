param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$OutputDirectory='artifacts/app'
)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    if ([IO.Path]::IsPathRooted($OutputDirectory)) { $output=[IO.Path]::GetFullPath($OutputDirectory) }
    else { $output=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
    $output=$output.TrimEnd('\','/')
    if ($PSScriptRoot.Equals($output,[StringComparison]::OrdinalIgnoreCase) -or $PSScriptRoot.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must be a dedicated package directory, not the repository or its parent.'
    }
    foreach ($sourceFolder in @('src','runtimes','.cache','.git')) {
        $source=Join-Path $PSScriptRoot $sourceFolder
        if ($output.Equals($source,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'OutputDirectory must not overwrite source, Git metadata, cache, or the acquired runtime.'
        }
    }
    $runtime=Join-Path $PSScriptRoot 'runtimes/powershell'
    & "$PSScriptRoot/acquire-terminal-runtime.ps1" -Offline -VerifyOnly
    $powerShellVersion='7.6.6'
    $archiveHash='02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860'
    $archive=Join-Path $PSScriptRoot ".cache/PowerShell-$powerShellVersion-win-x64.zip"
    if (!(Test-Path -LiteralPath $archive) -or !(Test-Path -LiteralPath (Join-Path $runtime 'pwsh.exe'))) {
        throw 'Pinned PowerShell archive/runtime is missing. Run acquire-runtime.ps1 as a build preparation step.'
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'PowerShell archive hash mismatch.' }
    # Check the extracted files too: an authentic ZIP alone does not prove the copied runtime is unmodified.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    $runtimeEntries=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            $relative=$entry.FullName.Replace('\','/')
            $null=$runtimeEntries.Add($relative)
            $file=Join-Path $runtime $relative
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Pinned runtime file missing: $relative" }
            $stream=$entry.Open()
            $sha=[Security.Cryptography.SHA256]::Create()
            try { $expected=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "Pinned runtime file changed: $relative" }
        }
        foreach ($file in Get-ChildItem -LiteralPath $runtime -File -Recurse) {
            $relative=$file.FullName.Substring($runtime.Length+1).Replace('\','/')
            if (!$runtimeEntries.Contains($relative)) { throw "Unpinned extra runtime file: $relative" }
        }
    } finally { $zip.Dispose() }
    dotnet build src/PS7Studio.PowerShell -c $Configuration
    if ($LASTEXITCODE) { throw 'Engine build failed.' }
    $packageBuildRoot=Join-Path $PSScriptRoot '.cache/package-build'
    dotnet publish src/PS7Studio.App -c $Configuration -o $output --self-contained true "-p:StudioBuildRoot=$packageBuildRoot"
    if ($LASTEXITCODE) { throw 'Application publish failed.' }
    dotnet msbuild src/PS7Studio.PowerShell/PS7Studio.PowerShell.csproj -t:BundleRuntime "-p:Configuration=$Configuration" "-p:BundleDirectory=$output"
    if ($LASTEXITCODE) { throw 'PowerShell runtime bundling failed.' }

    $notices=Join-Path $output 'notices'
    New-Item -ItemType Directory -Force -Path (Join-Path $notices 'PowerShell'),(Join-Path $notices 'dotnet') | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE.txt'),(Join-Path $runtime 'ThirdPartyNotices.txt') -Destination (Join-Path $notices 'PowerShell') -Force
    $assets=Get-Content src/PS7Studio.App/obj/project.assets.json -Raw | ConvertFrom-Json
    $packageRoots=@($assets.packageFolders.PSObject.Properties.Name)
    $dependencies=@()
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $parts=$library.Name.Split('/')
        $packagePath=$null
        foreach ($packageRoot in $packageRoots) {
            $candidate=Join-Path $packageRoot $library.Value.path
            if (Test-Path -LiteralPath $candidate) { $packagePath=$candidate; break }
        }
        if (!$packagePath) { throw "Dependency package missing from restored cache: $($library.Name)" }
        $noticeFiles=@()
        foreach ($relative in $library.Value.files) {
            if ($relative -notmatch '(?i)(^|/)[^/]*(license|notice)[^/]*\.(txt|md|html)$') { continue }
            $destinationRelative='notices/'+$library.Value.path+'/'+$relative
            $destination=Join-Path $output $destinationRelative
            New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
            Copy-Item -LiteralPath (Join-Path $packagePath $relative) -Destination $destination -Force
            $noticeFiles+=$destinationRelative
        }
        $dependencies += [ordered]@{ name=$parts[0]; version=$parts[1]; origin='NuGet restore'; notices=$noticeFiles }
    }
    $runtimeConfig=Get-Content -LiteralPath (Join-Path $output 'PS7Studio.runtimeconfig.json') -Raw | ConvertFrom-Json
    $dotnetVersion=($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    if (!$dotnetVersion) { throw 'Self-contained .NET runtime version was not found.' }
    $dotnetPackage=$null
    foreach ($packageRoot in $packageRoots) {
        $candidate=Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64/$dotnetVersion"
        if (Test-Path -LiteralPath $candidate) { $dotnetPackage=$candidate; break }
    }
    if (!$dotnetPackage) { throw 'Restored .NET runtime package is required to preserve its notices.' }
    Copy-Item -LiteralPath (Join-Path $dotnetPackage 'LICENSE.TXT'),(Join-Path $dotnetPackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $notices 'dotnet') -Force
    $dependencies += [ordered]@{ name='Microsoft.NETCore.App'; version=$dotnetVersion; origin='self-contained win-x64 runtime'; notices=@('notices/dotnet/LICENSE.TXT','notices/dotnet/THIRD-PARTY-NOTICES.TXT') }
    $dependencies += [ordered]@{ name='PowerShell'; version=$powerShellVersion; origin='official release ZIP'; notices=@('notices/PowerShell/LICENSE.txt','notices/PowerShell/ThirdPartyNotices.txt') }
    $terminalDependencies=@(
        [ordered]@{ name='PowerShellEditorServices'; version='4.7.0'; origin='official GitHub release ZIP'; source='https://github.com/PowerShell/PowerShellEditorServices/releases/download/v4.7.0/PowerShellEditorServices.zip'; archiveSha256='5084f0326cc88539e9d880b08f6c52ce5be4672bb8ecee8921e35f8895a6b9d7'; notices=@('runtimes/editorservices/LICENSE','runtimes/editorservices/NOTICE.txt') },
        [ordered]@{ name='@xterm/xterm'; version='6.0.0'; origin='npm registry'; source='https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz'; archiveSha256='908e66e04af6c8dc6b00dd3b54de088e2e81e5ed866284fd6c2fb3c2d1c7a3f6'; notices=@('Assets/Terminal/vendor/LICENSE.xterm') },
        [ordered]@{ name='@xterm/addon-fit'; version='0.11.0'; origin='npm registry'; source='https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz'; archiveSha256='26003b4517a132b64e4ff228fd88a5fda3fff5e606c76093f6dcff772e9ecec0'; notices=@('Assets/Terminal/vendor/LICENSE.addon-fit') }
    )
    $dependencies += $terminalDependencies
    $files=@(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object Name -ne 'dependency-manifest.json' | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=$_.FullName.Substring($output.TrimEnd('\','/').Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{
        schemaVersion=1; generatedUtc=[DateTime]::UtcNow.ToString('o'); architecture='x64'
        applicationVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output 'PS7Studio.exe')).ProductVersion
        powerShell=[ordered]@{ version=$powerShellVersion; archiveSha256=$archiveHash; source="https://github.com/PowerShell/PowerShell/releases/download/v$powerShellVersion/PowerShell-$powerShellVersion-win-x64.zip" }
        prerequisites=@([ordered]@{ name='Microsoft.WebView2.EvergreenRuntime'; installation='external'; required=$true; download='https://developer.microsoft.com/en-us/microsoft-edge/webview2/'; applicationDownloadsRuntime=$false })
        dependencies=$dependencies; files=$files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'dependency-manifest.json') -Encoding UTF8
    Write-Output "Application: $output/PS7Studio.exe"
} finally {Pop-Location}
