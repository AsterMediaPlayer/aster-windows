# Downloads libmpv (the mpv player as a library) into native\win-x64, where
# the build copies it next to the app. The DLL is about 100 MB, too big to
# keep in git, so a pinned build is fetched and checked against its hash.
#
# Builds come from shinchiro/mpv-winbuild-cmake, the Windows builds mpv.io
# links to. To move to a newer one, change $Tag, $Asset and $Sha256 to a
# release's "mpv-dev-x86_64-…" asset (not "-v3", which needs AVX2).

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$Tag = '20261020'
$Asset = 'mpv-dev-x86_64-20261020-git-6a262d11f6.7z'
$Sha256 = 'd16ea67bcc46d4ee9170f05b322d7184768f216f458bef089c3397a6beaff83e'

$target = Join-Path $PSScriptRoot '..\native\win-x64'
$dll = Join-Path $target 'libmpv-2.dll'
if ((Test-Path $dll) -and -not $Force) { return }

$work = Join-Path ([IO.Path]::GetTempPath()) ("aster-libmpv-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $archive = Join-Path $work $Asset
    $url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$Asset"
    Write-Host "Downloading libmpv $Tag..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    # .NET's SHA-256 rather than Get-FileHash: started from PowerShell 7 (as
    # GitHub's runners and many terminals do), Windows PowerShell inherits a
    # module path where Get-FileHash can't be found.
    $stream = [IO.File]::OpenRead($archive)
    try { $actual = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose() }
    if ($actual -ne $Sha256) { throw "libmpv download has the wrong hash ($actual); refusing to use it." }

    # Windows' own tar (libarchive) reads 7z.
    & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $work libmpv-2.dll
    if ($LASTEXITCODE -ne 0) { throw "Couldn't unpack $Asset." }

    New-Item -ItemType Directory -Force $target | Out-Null
    Move-Item -Force (Join-Path $work 'libmpv-2.dll') $dll
    Write-Host "libmpv is in $target"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
