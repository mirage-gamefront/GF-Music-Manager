[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must stay under the repository root: $fullPath"
    }

    return $fullPath
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[^\\/]+$')]
        [string]$RootDirectoryName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fixedTimestamp = [System.DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    $fileStream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)

    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $fileStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $files = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
                Sort-Object { $_.FullName.Substring($sourceRoot.Length) }

            foreach ($file in $files) {
                $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar)
                $entryName = $RootDirectoryName + '/' +
                    $relativePath.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
                $entry = $archive.CreateEntry(
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp

                $input = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

function Copy-ReleaseSourceItem {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $resolvedSource = [System.IO.Path]::GetFullPath($SourcePath)
    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $relativeSource = [System.IO.Path]::GetRelativePath($resolvedRepositoryRoot, $resolvedSource)

    if (Test-Path -LiteralPath $resolvedSource -PathType Leaf) {
        $destination = Join-Path $DestinationRoot $relativeSource
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $resolvedSource -Destination $destination -Force
        return
    }

    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Container)) {
        throw "Release source path was not found: $resolvedSource"
    }

    Get-ChildItem -LiteralPath $resolvedSource -Recurse -File |
        Where-Object {
            $relativeFile = [System.IO.Path]::GetRelativePath($resolvedSource, $_.FullName)
            $relativeFile -notmatch '(^|[\\/])(bin|obj|tmp|artifacts|TestResults|\.vs|\.git|\.tmp)([\\/]|$)'
        } |
        ForEach-Object {
            $relativeFile = [System.IO.Path]::GetRelativePath($resolvedRepositoryRoot, $_.FullName)
            $destination = Join-Path $DestinationRoot $relativeFile
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
}

function Assert-NoForbiddenReleaseFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $forbiddenFiles = Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object {
            $_.Name -match '(?i)(^settings\.json$|^modlist\.txt$|^plugins\.txt$|^loadorder\.txt$|\.log$|\.pdb$|^\.diagnostic|draft.*\.json$)' -or
            $_.Extension -match '(?i)^\.(esp|esm|esl|xwm|wav|mp3|ogg|flac)$'
        }

    if ($forbiddenFiles) {
        $relativeFiles = $forbiddenFiles | ForEach-Object {
            [System.IO.Path]::GetRelativePath($Root, $_.FullName)
        }
        throw "Forbidden release files were found: $($relativeFiles -join ', ')"
    }
}

function New-RootAppHost {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishedAppHostPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[^\\/]+$')]
        [string]$DependencyDirectoryName,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[^\\/]+\.dll$')]
        [string]$ManagedAssemblyName
    )

    $sourceBytes = [System.IO.File]::ReadAllBytes($PublishedAppHostPath)
    $sourcePathBytes = [System.Text.Encoding]::UTF8.GetBytes($ManagedAssemblyName)
    $destinationPathBytes = [System.Text.Encoding]::UTF8.GetBytes(
        "$DependencyDirectoryName\\$ManagedAssemblyName")
    $matches = [System.Collections.Generic.List[int]]::new()

    for ($offset = 0; $offset -le $sourceBytes.Length - $sourcePathBytes.Length; $offset++) {
        $isMatch = $true
        for ($index = 0; $index -lt $sourcePathBytes.Length; $index++) {
            if ($sourceBytes[$offset + $index] -ne $sourcePathBytes[$index]) {
                $isMatch = $false
                break
            }
        }

        if ($isMatch -and
            $offset + $sourcePathBytes.Length -lt $sourceBytes.Length -and
            $sourceBytes[$offset + $sourcePathBytes.Length] -eq 0) {
            $matches.Add($offset)
        }
    }

    if ($matches.Count -ne 1) {
        throw "Expected one embedded managed assembly path in apphost, found $($matches.Count): $PublishedAppHostPath"
    }

    $pathOffset = $matches[0]
    for ($index = $sourcePathBytes.Length; $index -le $destinationPathBytes.Length; $index++) {
        if ($sourceBytes[$pathOffset + $index] -ne 0) {
            throw "The embedded apphost path buffer is too short for $DependencyDirectoryName\\$ManagedAssemblyName"
        }
    }

    [Array]::Copy($destinationPathBytes, 0, $sourceBytes, $pathOffset, $destinationPathBytes.Length)
    $sourceBytes[$pathOffset + $destinationPathBytes.Length] = 0
    [System.IO.File]::WriteAllBytes($DestinationPath, $sourceBytes)
}

function Assert-BinaryPackageLayout {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$DependencyDirectoryName
    )

    $rootFiles = @(Get-ChildItem -LiteralPath $Root -File)
    if ($rootFiles.Count -ne 1 -or $rootFiles[0].Name -cne 'GfMusicManager.exe') {
        throw "Binary package root must contain only GfMusicManager.exe"
    }

    $dependencyDirectory = Join-Path $Root $DependencyDirectoryName
    $requiredDependencyFiles = @(
        'GfMusicManager.dll',
        'GfMusicManager.deps.json',
        'GfMusicManager.runtimeconfig.json'
    )
    foreach ($requiredFile in $requiredDependencyFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $dependencyDirectory $requiredFile) -PathType Leaf)) {
            throw "Required dependency file was not found: $DependencyDirectoryName\\$requiredFile"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $dependencyDirectory 'GfMusicManager.exe')) {
        throw "The internal apphost must not remain under $DependencyDirectoryName"
    }
}

function Assert-NoLocalEnvironmentReferences {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $textExtensions = @('.cs', '.csproj', '.json', '.md', '.ps1', '.pubxml', '.txt', '.xaml', '.xml')
    $windowsPathBranches = @(
        'Users\\[^\\\r\n]+',
        'Work\\',
        'SkyModding\\',
        'Modding\\MO2'
    ) -join '|'
    $unixUserPath = '/' + 'Users/' + '[^/\r\n]+/'
    $unixHomePath = '/' + 'home/' + '[^/\r\n]+/'
    $localPathPattern = "(?i)([A-Z]:\\(?:$windowsPathBranches)|$unixUserPath|$unixHomePath)"
    $literalReferences = @(
        [System.IO.Path]::GetFullPath($RepositoryRoot),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $findings = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $_.FullName)
        if ($textExtensions -contains $_.Extension.ToLowerInvariant()) {
            $content = [System.IO.File]::ReadAllText($_.FullName)
            if ([System.Text.RegularExpressions.Regex]::IsMatch($content, $localPathPattern)) {
                $findings.Add($relativePath)
            }

            foreach ($literalReference in $literalReferences) {
                $normalizedReference = $literalReference.Replace('\', '/')
                if ($content.Contains($literalReference, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $content.Contains($normalizedReference, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $findings.Add($relativePath)
                }
            }
        }
        elseif ($_.Name -match '^(?i)(GfMusicManager.*\.(dll|exe)|SkyrimScan\.Core\.dll)$') {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            $latinContent = [System.Text.Encoding]::Latin1.GetString($bytes)
            $unicodeContent = [System.Text.Encoding]::Unicode.GetString($bytes)
            if ([System.Text.RegularExpressions.Regex]::IsMatch($latinContent, $localPathPattern) -or
                [System.Text.RegularExpressions.Regex]::IsMatch($unicodeContent, $localPathPattern)) {
                $findings.Add($relativePath)
            }

            foreach ($literalReference in $literalReferences) {
                $normalizedReference = $literalReference.Replace('\', '/')
                if ($latinContent.Contains($literalReference, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $latinContent.Contains($normalizedReference, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $unicodeContent.Contains($literalReference, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $unicodeContent.Contains($normalizedReference, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $findings.Add($relativePath)
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        throw "Local environment references were found: $(($findings | Sort-Object -Unique) -join ', ')"
    }
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'src\GfMusicManager\Desktop\GfMusicManager.Desktop.csproj'
$readmePath = Join-Path $repoRoot 'README-GF-Music-Manager.md'
$readmeEnglishPath = Join-Path $repoRoot 'README-GF-Music-Manager.en.md'
$licensePath = Join-Path $repoRoot 'LICENSE-GF-MUSIC-MANAGER.txt'
$thirdPartyNoticesPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES-GF-MUSIC-MANAGER.txt'
$thirdPartyLicenseDirectory = Join-Path $repoRoot 'licenses\GfMusicManager'
$artifactBase = Assert-PathUnderRoot -Path (Join-Path $repoRoot 'artifacts\GfMusicManager') -Root $repoRoot
$releaseDirectory = Assert-PathUnderRoot -Path (Join-Path $artifactBase $Version) -Root $repoRoot
$temporaryDirectory = Assert-PathUnderRoot -Path (
    Join-Path $artifactBase ('.tmp-' + $Version + '-' + $PID)) -Root $repoRoot

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file was not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    throw "Release README was not found: $readmePath"
}

if (-not (Test-Path -LiteralPath $readmeEnglishPath -PathType Leaf)) {
    throw "English release README was not found: $readmeEnglishPath"
}

if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "GF Music Manager license was not found: $licensePath"
}

if (-not (Test-Path -LiteralPath $thirdPartyNoticesPath -PathType Leaf)) {
    throw "GF Music Manager third-party notices were not found: $thirdPartyNoticesPath"
}

if (-not (Test-Path -LiteralPath $thirdPartyLicenseDirectory -PathType Container)) {
    throw "GF Music Manager third-party license directory was not found: $thirdPartyLicenseDirectory"
}

if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $temporaryDirectory) {
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

$packages = @(
    [pscustomobject]@{
        Name = 'win-x64-framework-dependent'
        PublishProfile = 'win-x64-framework-dependent'
    },
    [pscustomobject]@{
        Name = 'win-x64-self-contained'
        PublishProfile = 'win-x64-self-contained'
    }
)

try {
    foreach ($package in $packages) {
        $publishDirectory = Assert-PathUnderRoot -Path (
            Join-Path $temporaryDirectory ($package.Name + '-publish')) -Root $repoRoot
        $packageDirectory = Assert-PathUnderRoot -Path (
            Join-Path $temporaryDirectory ($package.Name + '-package')) -Root $repoRoot
        $dependencyDirectoryName = 'dll'
        $dependencyDirectory = Join-Path $packageDirectory $dependencyDirectoryName

        & dotnet publish $projectPath `
            --configuration Release `
            --runtime win-x64 `
            --output $publishDirectory `
            --nologo `
            -p:PublishProfile=$($package.PublishProfile) `
            -p:Version=$Version `
            -p:ContinuousIntegrationBuild=true `
            -p:PathMap="$repoRoot=/_/"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $($package.Name) with exit code $LASTEXITCODE"
        }

        Get-ChildItem -LiteralPath $publishDirectory -Recurse -Filter '*.pdb' -File |
            Remove-Item -Force

        New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $publishDirectory -Force |
            Copy-Item -Destination $dependencyDirectory -Recurse -Force

        $publishedAppHostPath = Join-Path $dependencyDirectory 'GfMusicManager.exe'
        New-RootAppHost `
            -PublishedAppHostPath $publishedAppHostPath `
            -DestinationPath (Join-Path $packageDirectory 'GfMusicManager.exe') `
            -DependencyDirectoryName $dependencyDirectoryName `
            -ManagedAssemblyName 'GfMusicManager.dll'
        Remove-Item -LiteralPath $publishedAppHostPath -Force

        $documentationDirectory = Join-Path $packageDirectory 'Documentation'
        New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $readmeEnglishPath -Destination (
            Join-Path $documentationDirectory 'README.md') -Force
        Copy-Item -LiteralPath $readmePath -Destination (
            Join-Path $documentationDirectory 'README.ja.md') -Force
        Copy-Item -LiteralPath $licensePath -Destination (
            Join-Path $documentationDirectory 'LICENSE.txt') -Force
        Copy-Item -LiteralPath $thirdPartyNoticesPath -Destination (
            Join-Path $documentationDirectory 'THIRD-PARTY-NOTICES.txt') -Force
        Copy-Item -LiteralPath $thirdPartyLicenseDirectory -Destination (
            Join-Path $documentationDirectory 'LICENSES') -Recurse -Force

        Assert-BinaryPackageLayout `
            -Root $packageDirectory `
            -DependencyDirectoryName $dependencyDirectoryName
        Assert-NoForbiddenReleaseFiles -Root $packageDirectory
        Assert-NoLocalEnvironmentReferences -Root $packageDirectory -RepositoryRoot $repoRoot

        $zipName = "GF-Music-Manager-v$Version-$($package.Name).zip"
        $zipPath = Assert-PathUnderRoot -Path (Join-Path $releaseDirectory $zipName) -Root $repoRoot
        New-DeterministicZip `
            -SourceDirectory $packageDirectory `
            -DestinationPath $zipPath `
            -RootDirectoryName 'GF Music Manager'
    }

    $sourceDirectory = Assert-PathUnderRoot -Path (
        Join-Path $temporaryDirectory 'source') -Root $repoRoot
    New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null

    $sourceItems = @(
        'global.json',
        'README-GF-Music-Manager.md',
        'README-GF-Music-Manager.en.md',
        'LICENSE-GF-MUSIC-MANAGER.txt',
        'THIRD-PARTY-NOTICES-GF-MUSIC-MANAGER.txt',
        'licenses\GfMusicManager',
        'assets\branding',
        'src\Common\SkyrimScan.Core',
        'src\GfMusicManager',
        'tests\SkyrimScan.Core.Tests',
        'tests\GfMusicManager.Core.Tests',
        'tests\GfMusicManager.Desktop.Tests',
        'tools\build-gf-music-manager-release.ps1'
    )

    foreach ($sourceItem in $sourceItems) {
        Copy-ReleaseSourceItem `
            -SourcePath (Join-Path $repoRoot $sourceItem) `
            -DestinationRoot $sourceDirectory `
            -RepositoryRoot $repoRoot
    }

    Assert-NoForbiddenReleaseFiles -Root $sourceDirectory
    Assert-NoLocalEnvironmentReferences -Root $sourceDirectory -RepositoryRoot $repoRoot

    $sourceZipName = "GF-Music-Manager-v$Version-source.zip"
    $sourceZipPath = Assert-PathUnderRoot -Path (
        Join-Path $releaseDirectory $sourceZipName) -Root $repoRoot
    New-DeterministicZip `
        -SourceDirectory $sourceDirectory `
        -DestinationPath $sourceZipPath `
        -RootDirectoryName 'GF Music Manager Source'

    $checksumLines = Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.zip' -File |
        Sort-Object Name |
        ForEach-Object {
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
        }
    $checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding Ascii

    Write-Host "GF Music Manager release packages created:"
    Get-ChildItem -LiteralPath $releaseDirectory -File |
        Sort-Object Name |
        ForEach-Object { Write-Host "  $($_.FullName)" }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
