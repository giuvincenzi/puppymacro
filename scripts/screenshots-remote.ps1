# make screenshots-remote (on Windows; in WSL the Makefile runs the same steps in its shell, where git and gh are Linux's):
# starts the Screenshots workflow on GitHub on the current branch, waits for it and puts its screenshots in site/
# (site/guide/img and site/assets/loops.png), to check with git diff and commit. The PC stays free meanwhile.
# The branch must be pushed: the workflow takes the code from GitHub.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$branch = git -C $root rev-parse --abbrev-ref HEAD
git -C $root fetch --quiet origin $branch
if ((git -C $root rev-parse HEAD) -ne (git -C $root rev-parse "origin/$branch")) {
    throw "Push $branch first: the workflow takes the code from GitHub."
}

# return_run_details: the API answers with the Id of the run it created (GitHub changelog, 2026-02-19).
$runId = gh api --method POST 'repos/{owner}/{repo}/actions/workflows/screenshots.yml/dispatches' `
    -f "ref=$branch" -F return_run_details=true -q .workflow_run_id
if ($LASTEXITCODE -ne 0 -or -not $runId) { throw "The Screenshots workflow did not start." }
Write-Host "Screenshots running on GitHub: run $runId"

gh run watch $runId --exit-status
if ($LASTEXITCODE -ne 0) { throw "The Screenshots workflow failed: gh run view $runId --log-failed" }
# The artifact keeps the paths under site/ (guide/img/…, assets/loops.png); downloaded apart, then copied over the old ones.
$download = Join-Path ([System.IO.Path]::GetTempPath()) "puppymacro-screenshots-$runId"
gh run download $runId --name screenshots --dir $download
if ($LASTEXITCODE -ne 0) { throw "Could not download the screenshots of run $runId." }
Copy-Item -Path (Join-Path $download '*') -Destination (Join-Path $root 'site') -Recurse -Force
Remove-Item $download -Recurse -Force
Write-Host "Screenshots in site/guide/img and site/assets/loops.png: check them with git diff, then commit."
