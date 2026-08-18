param(
    [switch]$SkipTests,
    [switch]$BuildInstaller,
    [switch]$BuildPortable
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $MyInvocation.MyCommand.Path
$nugetConfig = Join-Path $workspace 'NuGet.Config'
$publishDirectory = Join-Path $workspace 'artifacts\publish\win-x64'

dotnet restore (Join-Path $workspace 'IrisQuickQuery.sln') --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
if (-not $SkipTests) {
    dotnet test (Join-Path $workspace 'tests\IrisQuickQuery.Core.Tests\IrisQuickQuery.Core.Tests.csproj') --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}
if (Test-Path -LiteralPath $publishDirectory) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDirectory).Path
    $allowedPublishRoot = (Join-Path $workspace 'artifacts\publish')
    if (-not $resolvedPublish.StartsWith($allowedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected publish path: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
dotnet publish (Join-Path $workspace 'src\IrisQuickQuery.App\IrisQuickQuery.App.csproj') --no-restore -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if ($BuildInstaller) {
    $isccCandidates = @(
        (Join-Path $workspace '.tools\InnoSetup7\ISCC.exe'),
        (Join-Path $workspace '.tools\InnoSetup6\ISCC.exe'),
        'D:\Tools\InnoSetup7\ISCC.exe',
        'D:\Tools\InnoSetup6\ISCC.exe',
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup compiler is not installed. Publish output is ready, but installer compilation cannot continue.' }
    & $iscc (Join-Path $workspace 'installer\IrisQuickQuery.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
}

if ($BuildPortable) {
    $appProjectPath = Join-Path $workspace 'src\IrisQuickQuery.App\IrisQuickQuery.App.csproj'
    [xml]$appProject = Get-Content -LiteralPath $appProjectPath -Raw
    $appVersion = [string]($appProject.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($appVersion)) { throw 'Application version is missing from IrisQuickQuery.App.csproj.' }

    $portableRoot = Join-Path $workspace 'artifacts\portable'
    $portableName = "IrisQuickQuery-Portable-$appVersion-win-x64"
    $portableDirectory = Join-Path $portableRoot $portableName
    $portableArchive = Join-Path $portableRoot "$portableName.zip"
    $portableRootFull = [IO.Path]::GetFullPath($portableRoot).TrimEnd('\') + '\'
    $portableDirectoryFull = [IO.Path]::GetFullPath($portableDirectory)
    if (-not $portableDirectoryFull.StartsWith($portableRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to prepare unexpected portable path: $portableDirectoryFull"
    }

    if (Test-Path -LiteralPath $portableDirectory) {
        $resolvedPortable = (Resolve-Path -LiteralPath $portableDirectory).Path
        if (-not $resolvedPortable.StartsWith($portableRootFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected portable path: $resolvedPortable"
        }
        Remove-Item -LiteralPath $resolvedPortable -Recurse -Force
    }
    New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $portableDirectory -Recurse -Force
    $portableReadmeSource = Join-Path $workspace 'portable\README-PORTABLE.txt'
    $portableReadmeDestination = Join-Path $portableDirectory 'README-PORTABLE.txt'
    if (-not (Test-Path -LiteralPath $portableReadmeSource)) { throw "Portable README is missing: $portableReadmeSource" }
    Copy-Item -LiteralPath $portableReadmeSource -Destination $portableReadmeDestination -Force
    if (-not (Test-Path -LiteralPath $portableReadmeDestination)) { throw 'Portable README was not copied into the package.' }

    if (Test-Path -LiteralPath $portableArchive) {
        Remove-Item -LiteralPath $portableArchive -Force
    }
    Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal
    Write-Host "Portable package completed: $portableArchive"
}

Write-Host "Publish completed: $publishDirectory"
