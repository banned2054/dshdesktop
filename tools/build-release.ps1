param(
    [Parameter(Mandatory)][string]$UpstreamDir,
    [Parameter(Mandatory)][string]$OutputRoot
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

Push-Location $repoDir
try {
    node tools/release.mjs source $sourceDir
    if ($LASTEXITCODE -ne 0) { throw 'Pinned DSH source validation failed' }

    dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o $bundleDir
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed' }

    Push-Location $sourceDir
    try {
        # The pinned upstream uses linked workspaces; pnpm 11 requires legacy deploy.
        pnpm --filter @deepseek-ai/dsh-desktop-host deploy --legacy --prod $deployDir
        if ($LASTEXITCODE -ne 0) { throw 'DSH production dependency export failed' }
        # Reuse upstream's checksum-locked Node/Python/pnpm and Office assets.
        pnpm run prepare:primary-runtime --target win-x64 --output $resourceDir --cache (Join-Path $destination 'downloads')
        if ($LASTEXITCODE -ne 0) { throw 'Primary runtime preparation failed' }
    }
    finally { Pop-Location }

    node tools/release.mjs stage $sourceDir $deployDir $bundleDir
    if ($LASTEXITCODE -ne 0) { throw 'Runtime staging failed' }

    $version = ([xml](Get-Content -LiteralPath DshDesktop/DshDesktop.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
    $archiveName = "dsh-desktop-$version-win-x64.zip"
    $archivePath = Join-Path $destination $archiveName
    # Windows' bsdtar handles the large runtime and long paths better than Compress-Archive.
    tar -a -c -f $archivePath -C $bundleDir .
    if ($LASTEXITCODE -ne 0) { throw 'ZIP creation failed' }

    # Smoke the extracted ZIP, so workspace links and archive omissions cannot hide.
    $checkDir = Join-Path $destination 'extracted package'
    New-Item -ItemType Directory -Path $checkDir | Out-Null
    tar -x -f $archivePath -C $checkDir
    if ($LASTEXITCODE -ne 0) { throw 'ZIP extraction failed' }
    node tools/release.mjs smoke $checkDir
    if ($LASTEXITCODE -ne 0) { throw 'Extracted runtime smoke test failed' }

    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archivePath.sha256", "$checksum  $archiveName`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Package ready: $archivePath"
}
finally { Pop-Location }
