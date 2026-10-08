param(
    [Parameter(Mandatory)][string]$UpstreamDir,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'osx-arm64', 'linux-x64')][string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoDir = Split-Path $PSScriptRoot -Parent
$sourceDir = (Resolve-Path -LiteralPath $UpstreamDir).Path
$destination = [System.IO.Path]::GetFullPath($OutputRoot)
# Never remove or overwrite an existing packaging directory.
if (Test-Path -LiteralPath $destination) { throw "Output directory must be new: $destination" }
New-Item -ItemType Directory -Path $destination | Out-Null
$bundleDir = Join-Path $destination 'package'
$deployDir = Join-Path $destination 'deployed-host'
$resourceDir = Join-Path $bundleDir 'backend'
$runtimeTarget = @{ 'win-x64' = 'win-x64'; 'osx-arm64' = 'mac-arm64'; 'linux-x64' = 'linux-x64' }[$Rid]

Push-Location $repoDir
try {
    node tools/release.mjs source $sourceDir
    if ($LASTEXITCODE -ne 0) { throw 'Pinned DSH source validation failed' }

    dotnet publish DshDesktop/DshDesktop.csproj -c Release -r $Rid --self-contained true -p:PublishAot=true -o $bundleDir
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed' }

    Push-Location $sourceDir
    try {
        # Prepare before deploy: pnpm 11 legacy deploy records production-only workspace state.
        pnpm run prepare:primary-runtime --target $runtimeTarget --output $resourceDir --cache (Join-Path $destination 'downloads')
        if ($LASTEXITCODE -ne 0) { throw 'Primary runtime preparation failed' }
        # The pinned upstream uses linked workspaces; pnpm 11 requires legacy deploy.
        pnpm --filter @deepseek-ai/dsh-desktop-host deploy --legacy --prod $deployDir
        if ($LASTEXITCODE -ne 0) { throw 'DSH production dependency export failed' }
    }
    finally { Pop-Location }

    node tools/release.mjs stage $sourceDir $deployDir $bundleDir $Rid
    if ($LASTEXITCODE -ne 0) { throw 'Runtime staging failed' }

    # Keep generated symbols for diagnostics, outside the user-facing ZIP.
    node tools/release.mjs symbols $bundleDir (Join-Path $destination 'symbols')
    if ($LASTEXITCODE -ne 0) { throw 'Debug symbol separation failed' }

    $version = ([xml](Get-Content -LiteralPath DshDesktop/DshDesktop.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
    $archiveName = "dsh-desktop-$version-$Rid.zip"
    $archivePath = Join-Path $destination $archiveName
    if ($Rid -eq 'osx-arm64' -or $Rid -eq 'linux-x64') {
        Push-Location $bundleDir
        try { zip -q -r $archivePath . }
        finally { Pop-Location }
    }
    else {
        tar -a -c -f $archivePath -C $bundleDir .
    }
    if ($LASTEXITCODE -ne 0) { throw 'ZIP creation failed' }

    # Smoke the extracted ZIP, so workspace links and archive omissions cannot hide.
    $checkDir = Join-Path $destination 'extracted package'
    New-Item -ItemType Directory -Path $checkDir | Out-Null
    if ($Rid -eq 'win-x64') { tar -x -f $archivePath -C $checkDir }
    else { unzip -q $archivePath -d $checkDir }
    if ($LASTEXITCODE -ne 0) { throw 'ZIP extraction failed' }
    node tools/release.mjs smoke $checkDir $Rid
    if ($LASTEXITCODE -ne 0) { throw 'Extracted runtime smoke test failed' }

    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archivePath.sha256", "$checksum  $archiveName`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Package ready: $archivePath"
}
finally { Pop-Location }
