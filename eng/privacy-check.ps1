[CmdletBinding()]
param(
    [switch]$Staged,
    [switch]$IncludeUntracked
)

$ErrorActionPreference = 'Stop'

$repoRoot = (git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw 'privacy-check must run inside a Git repository' }

$files = if ($Staged) {
    @(git diff --cached --name-only --diff-filter=ACMR)
} else {
    @(git ls-files)
}

if ($IncludeUntracked -and -not $Staged) {
    $files += @(git ls-files --others --exclude-standard)
}

$files = @($files | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$failures = [System.Collections.Generic.List[string]]::new()

$forbiddenPaths = [ordered]@{
    'runtime artifact directory' = '(?i)(^|/)(artifacts|snapshots|recordings|runs|logs|live-tests|probe-artifacts|captures|screenshots|evidence|dumps|profiles|bin|obj|TestResults)(/|$)'
    'runtime artifact file'      = '(?i)\.(jsonl|log|har|dmp|dump|etl|trace|pcap|pcapng|sqlite3?|db|bin|raw|mem|core)$'
    'local configuration'        = '(?i)(^|/)(\.env(?:\..+)?|.+\.local\..+|baseline\.local\.json|character-selection\.json|config\.json|settings\.json)$'
}

foreach ($file in $files) {
    $normalized = $file -replace '\\', '/'
    foreach ($entry in $forbiddenPaths.GetEnumerator()) {
        if ($normalized -match $entry.Value) {
            $failures.Add("$file [$($entry.Key)]")
            break
        }
    }
}

$contentPatterns = [ordered]@{
    'local user path' = '(?i)(?:[A-Z]:\\Users\\[^\\\s''"]+|/(?:Users|home)/[^/\s''"]+)'
    'email address' = '(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b'
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'access token' = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|xox[baprs]-[A-Za-z0-9-]{10,})'
    'hard-coded character/account identity' = '(?i)(?:["''](?:characterName|playerName|accountName|accountId|characterId|playerId)["'']\s*:\s*["''](?!(?:Example|Test|Sample|Placeholder))[^"'']+["'']|\b(?:ExpectedCharacter|CharacterName|PlayerName|AccountName|AccountId|CharacterId|PlayerId)\b\s*[,=:]\s*["''](?!(?:Example|Test|Sample|Placeholder))[^"''{][^"'']*["''])'
}

$textExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.json', '.yml', '.yaml', '.toml', '.ini',
    '.config', '.xml', '.md', '.txt', '.ps1', '.psm1', '.sh', '.bat', '.cmd', '.ts',
    '.tsx', '.js', '.jsx', '.css', '.html', '.sln', '.slnx'
)

foreach ($file in $files) {
    $normalized = $file -replace '\\', '/'
    if ($normalized -eq 'eng/privacy-check.ps1') { continue }
    if ([System.IO.Path]::GetExtension($normalized).ToLowerInvariant() -notin $textExtensions) { continue }

    $content = if ($Staged) {
        (git show ":$file" 2>$null) -join "`n"
    } else {
        $path = Join-Path $repoRoot $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        [System.IO.File]::ReadAllText($path)
    }

    foreach ($entry in $contentPatterns.GetEnumerator()) {
        if ($content -match $entry.Value) {
            $failures.Add("$file [$($entry.Key)]")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Privacy check failed:`n - " + (($failures | Sort-Object -Unique) -join "`n - "))
    exit 1
}

$scope = if ($Staged) {
    'staged content'
} elseif ($IncludeUntracked) {
    'tracked and commit-eligible untracked content'
} else {
    'tracked content'
}
Write-Host "Privacy check passed for $scope ($($files.Count) files)."
