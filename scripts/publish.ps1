# Publishes new master commits to the PUBLIC repo as incremental history — no secrets, no full history.
#
# The public repo's `main` is rooted at a clean, secret-free "Initial public release" snapshot (the old
# dev history contains commits with real mesh/DB credentials and must never be pushed). This script keeps
# the public branch (`public-main`) growing by CHERRY-PICKING every master commit made since the last
# publish onto it — cherry-pick replays only the diffs, so no historical secret blob comes along — then
# fast-forward pushes it to public/main. The `last-published` tag marks how far master has been mirrored.
#
#   .\scripts\publish.ps1
#
# First-time setup (once): tag the master commit whose tree matches the current public snapshot:
#   git tag last-published <that-sha>
#
# CAVEAT: every master commit after the tag becomes public. Secrets live only in the git-ignored
# appsettings.Development.json (never committed), so normal commits are safe — but don't commit anything
# private to master that you don't want mirrored.

param(
    [string]$Remote = "public",
    [string]$PublicBranch = "public-main",
    [string]$Marker = "last-published"
)
$ErrorActionPreference = "Stop"

git rev-parse --verify "refs/tags/$Marker" *> $null
if ($LASTEXITCODE) { throw "Tag '$Marker' not found. Set it once: git tag $Marker <sha matching the public snapshot>." }

$new = git rev-list --reverse "$Marker..master"
if (-not $new) { Write-Host "Nothing new to publish (public is up to date with master)."; return }
$count = ($new | Measure-Object).Count

git switch $PublicBranch
try {
    git cherry-pick "$Marker..master"
    if ($LASTEXITCODE) { throw "cherry-pick conflict — resolve it, 'git cherry-pick --continue', push '$PublicBranch:main', then 'git tag -f $Marker master'." }
    git push $Remote "${PublicBranch}:main"
    if ($LASTEXITCODE) { throw "push to $Remote failed." }
    git tag -f $Marker master *> $null
    Write-Host "Published $count commit(s) to $Remote/main."
}
finally {
    git switch master *> $null
}
