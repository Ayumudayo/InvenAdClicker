param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$VersionString
)

if (-not $VersionString) {
    Write-Error "Version string is required as the first argument. e.g., 3.0.0"
    exit 1
}

$tagName = "v$VersionString"

# Ensure tags are up to date
git fetch --tags --quiet

# Check if tag already exists locally or remotely
$localTagExists = (& git rev-parse -q --verify "refs/tags/$tagName" 2>$null) -ne $null
$remoteTagExists = (& git ls-remote --tags origin "refs/tags/$tagName") -ne $null
if ($localTagExists -or $remoteTagExists) {
    Write-Error "Tag already exists: $tagName"
    exit 1
}

# Create annotated tag at current HEAD
git tag -a $tagName -m "$tagName"

# Push only the tag to origin (no branch push required)
git push origin $tagName

Write-Host "Done: Created and pushed annotated tag ($tagName)."

