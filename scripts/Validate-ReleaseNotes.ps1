param(
    [string]$ProjectPath = "EveCommandCenter.csproj",
    [string]$NotesPath = "RELEASE_NOTES.md",
    [string]$Tag = ""
)
$ErrorActionPreference = "Stop"
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
$versionNode = $project.SelectSingleNode("/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "Project must declare its release Version."
}
$version = $versionNode.InnerText.Trim()
$notes = Get-Content -LiteralPath $NotesPath -Raw -Encoding UTF8
$lines = $notes -split "`r?`n"
$expectedHeading = "# EVE Command Center v$version"
if ($lines[0].Trim() -cne $expectedHeading) {
    throw "Update $NotesPath for this patch. First line must be: $expectedHeading"
}
# Validate this release's own section, not a change list under an older heading.
$currentSection = ($lines | Select-Object -Skip 1) -join "`n"
$currentSection = ($currentSection -split '(?m)^#{1,2} ', 2)[0]
if ($currentSection -notmatch '(?m)^- [^
]*\S') {
    throw "Add a change list for v$version before any historical sections."
}
if ($Tag -and $Tag -cne "v$version") {
    throw "Release tag $Tag must match project and notes version v$version."
}
Write-Output "Release notes verified for v$version."
