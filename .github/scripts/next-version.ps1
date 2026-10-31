<#
.SYNOPSIS
Works out the next version from the conventional commits since the last v* tag.

.DESCRIPTION
"type!:" or a "BREAKING CHANGE:" footer -> major, "feat:" -> minor, "fix:" / "perf:" -> patch.
Types are matched case-insensitively ("Feat:" counts). Other commits (docs, chore, ci...) don't release.
Prints the version (without "v"), or nothing when there is nothing to release.
Run it in the repository; needs PowerShell 7 and git.

.PARAMETER NotesFile
Where to write the release notes (Markdown), if anything is released.
#>
param([string] $NotesFile)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # "no tag yet" is an exit code, not an error
[Console]::OutputEncoding = [Text.UTF8Encoding]::new() # commit messages come from git as UTF-8

$lastTag = git describe --tags --abbrev=0 --match 'v[0-9]*.[0-9]*.[0-9]*' 2>$null
if ($LASTEXITCODE -eq 0 -and $lastTag) {
    $range = "$lastTag..HEAD"
    $major, $minor, $patch = $lastTag.Substring(1).Split('.') | ForEach-Object { [int]$_ }
}
else {
    $range = 'HEAD'
    $major, $minor, $patch = 0, 0, 0
}

$log = git log --reverse --format='%s%x1f%b%x1e' $range
if ($LASTEXITCODE -ne 0) { throw "git log $range failed" }

$bump = 0 # 0 none, 1 patch, 2 minor, 3 major
$breaking = [Collections.Generic.List[string]]::new()
$features = [Collections.Generic.List[string]]::new()
$fixes = [Collections.Generic.List[string]]::new()

foreach ($commit in ($log -join "`n").Split([char]0x1e)) {
    $subject, $body = $commit.Trim("`n").Split([char]0x1f, 2)
    if ($subject -notmatch '^(?<type>[A-Za-z]+)(\([^)]*\))?(?<bang>!)?:\s*(?<description>.*)$') { continue }
    $type, $description = $Matches.type, $Matches.description

    if ($Matches.bang -or $body -cmatch '(?m)^BREAKING[ -]CHANGE:') {
        $breaking.Add($description)
        $bump = 3
    }
    elseif ($type -eq 'feat') {
        $features.Add($description)
        $bump = [Math]::Max($bump, 2)
    }
    elseif ($type -in 'fix', 'perf') {
        $fixes.Add($description)
        $bump = [Math]::Max($bump, 1)
    }
}

switch ($bump) {
    0 { return }
    1 { $patch++ }
    2 { $minor++; $patch = 0 }
    3 { $major++; $minor = 0; $patch = 0 }
}

if ($NotesFile) {
    $notes = foreach ($section in @{ Title = 'Breaking changes'; Items = $breaking },
                                  @{ Title = 'Features'; Items = $features },
                                  @{ Title = 'Fixes'; Items = $fixes }) {
        if ($section.Items.Count) {
            "### $($section.Title)", ''
            $section.Items | ForEach-Object { "- $_" }
            ''
        }
    }
    Set-Content -Path $NotesFile -Value $notes -Encoding utf8NoBOM
}

"$major.$minor.$patch"
