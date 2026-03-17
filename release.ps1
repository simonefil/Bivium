# Bivium release script
# Usage: .\release.ps1 -Tag "v1.0.0" -Notes "First release"

param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$Notes
)

$ErrorActionPreference = "Stop"

$project = "Bivium\Bivium.csproj"
$artifactsDir = "release-artifacts"
$publishDir = "publish"
$dockerImage = "draknodd/bivium"
$version = $Tag -replace "^v", ""

$rids = @(
    "win-x64",
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64"
)

function Confirm-Step {
    param([string]$Message)
    $choice = Read-Host "$Message [Y/n]"
    if ($choice -eq "n" -or $choice -eq "N") {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }
}

function Cleanup {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $artifactsDir) { Remove-Item $artifactsDir -Recurse -Force }
}

# Clean previous builds
Cleanup
New-Item -ItemType Directory -Path $artifactsDir | Out-Null

# Restore client-side libraries (Monaco, WebTUI, etc.)
Confirm-Step "Restore client-side libraries?"
Write-Host "Restoring client-side libraries..." -ForegroundColor Cyan
Push-Location Bivium
libman restore
Pop-Location
if ($LASTEXITCODE -ne 0) {
    Write-Host "libman restore failed" -ForegroundColor Red
    Cleanup
    exit 1
}

# Build all targets
Confirm-Step "Build binaries for $($rids.Count) platforms?"
foreach ($rid in $rids) {
    Write-Host "Building $rid..." -ForegroundColor Cyan

    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:EnableCompressionInSingleFile=true `
        -p:Version=$version `
        -o "$publishDir\$rid"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed for $rid" -ForegroundColor Red
        Cleanup
        exit 1
    }

    $zipName = "$artifactsDir\Bivium-$rid.zip"
    Compress-Archive -Path "$publishDir\$rid\*" -DestinationPath $zipName
    Write-Host "$rid done." -ForegroundColor Green
}

# Build Docker image
Confirm-Step "Build Docker image ${dockerImage}:${Tag}?"
Write-Host "Building Docker image..." -ForegroundColor Cyan
docker build -t "${dockerImage}:${Tag}" -t "${dockerImage}:latest" .
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker build failed" -ForegroundColor Red
    Cleanup
    exit 1
}

# Push Docker image
Confirm-Step "Push Docker image to Docker Hub?"
Write-Host "Pushing Docker image..." -ForegroundColor Cyan
docker push "${dockerImage}:${Tag}"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker push failed" -ForegroundColor Red
    Cleanup
    exit 1
}
docker push "${dockerImage}:latest"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker push failed for latest tag" -ForegroundColor Red
    Cleanup
    exit 1
}
Write-Host "Docker image pushed." -ForegroundColor Green

# Create and push tag
Confirm-Step "Create git tag $Tag and push?"
Write-Host "Creating tag $Tag..." -ForegroundColor Cyan
git tag $Tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create tag (already exists?)" -ForegroundColor Red
    Cleanup
    exit 1
}
git push origin $Tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to push tag, removing local tag..." -ForegroundColor Red
    git tag -d $Tag
    Cleanup
    exit 1
}

# Create GitHub release
Confirm-Step "Create GitHub release with artifacts?"
Write-Host "Creating GitHub release..." -ForegroundColor Cyan
$files = (Get-ChildItem $artifactsDir -Filter *.zip).FullName
gh release create $Tag $files --title $Tag --notes $Notes
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create release, removing tag..." -ForegroundColor Red
    git push origin --delete $Tag
    git tag -d $Tag
    Cleanup
    exit 1
}

Cleanup
Write-Host "Release $Tag published successfully." -ForegroundColor Green
