# PuppyMacro commands. They run on Windows (GNU make, see .claude/rules/build-and-release.md) and in WSL, where they
# use the Windows .NET SDK through Windows PowerShell.
#
#   make dev        build the current code and start it with fresh sample data (development build)
#   make dev-keep   same, keeping the development build's data from the last run
#   make test       run the unit tests
#   make e2e        run the end-to-end tests on the development build (do not use the PC meanwhile);
#                   make e2e CATEGORY=Macros runs one category (UI, Macros, Loops, Remaps, Recording, Overlay, AI)
#   make build      create the distribution in dist/ (Setup.exe + update packages), no upload
#   make docs       build the website in site-preview/ with the guide of the current code
#   make docs-serve same, then serve it at http://localhost:8080/ (Ctrl+C to stop)
#   make screenshots make dev (without the development build marks), then retake the guide's editor screenshots and the home page one (drives the UI),
#                   then close PuppyMacro
#   make screenshots-remote  the same screenshots taken on GitHub (the Screenshots workflow on the current branch, pushed),
#                   put in site/ when it ends: the PC stays free
#   make file-icons draw the icons of .puppyitems and .puppymacro files again (PuppyMacro/Assets/FileIcons);
#                   make file-icons PREVIEW=<folder> also writes each size as a PNG there
#   make release-pr open the release pull request (next version from the latest tag and CHANGELOG);
#                   merging it publishes the release
#   make release    retry the Release workflow on main (it never republishes an existing tag)
#   make clean      remove build output

ifeq ($(OS),Windows_NT)
POWERSHELL := powershell
else
POWERSHELL := powershell.exe
# WSL hands only the variables listed in WSLENV to Windows programs: the code signing ones (make build).
export WSLENV := CERTUM_EMAIL:CERTUM_OTP:CERTUM_TOKEN$(if $(WSLENV),:$(WSLENV))
endif

RUN_SCRIPT = $(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/$(1).ps1

.PHONY: dev dev-keep test e2e build docs docs-serve screenshots screenshots-remote file-icons release-pr release clean

dev:
	$(call RUN_SCRIPT,dev)

dev-keep:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Keep

test:
	$(call RUN_SCRIPT,test)

e2e:
	$(call RUN_SCRIPT,e2e) $(if $(CATEGORY),-Category $(CATEGORY))

build:
	$(call RUN_SCRIPT,build)

screenshots:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Screenshots
	$(call RUN_SCRIPT,screenshots)

file-icons:
	$(call RUN_SCRIPT,file-icons) $(if $(PREVIEW),-Preview '$(PREVIEW)')

docs:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/site.ps1 -Preview

docs-serve:
	$(call RUN_SCRIPT,site-serve)

# The Screenshots workflow on the current branch: the API answers with its run's Id (return_run_details), then gh waits
# for it and downloads its artifact into site/. On Windows in scripts/screenshots-remote.ps1; in WSL in this shell, where
# git and gh are Linux's.
screenshots-remote:
ifeq ($(OS),Windows_NT)
	$(call RUN_SCRIPT,screenshots-remote)
else
	@branch=$$(git rev-parse --abbrev-ref HEAD) && git fetch --quiet origin "$$branch" \
	  && { [ "$$(git rev-parse HEAD)" = "$$(git rev-parse "origin/$$branch")" ] || { echo "Push $$branch first: the workflow takes the code from GitHub."; exit 1; }; } \
	  && run=$$(gh api --method POST 'repos/{owner}/{repo}/actions/workflows/screenshots.yml/dispatches' -f "ref=$$branch" -F return_run_details=true -q .workflow_run_id) \
	  && [ -n "$$run" ] && echo "Screenshots running on GitHub: run $$run" \
	  && gh run watch "$$run" --exit-status \
	  && tmp=$$(mktemp -d) && gh run download "$$run" --name screenshots --dir "$$tmp" \
	  && cp -r "$$tmp"/. site/ && rm -rf "$$tmp" \
	  && echo "Screenshots in site/guide/img and site/assets/loops.png: check them with git diff, then commit."
endif

# Worktree .worktrees/release from origin/main, branch "release": scripts/release-pr.ps1 edits the
# files, git and gh commit, push and open the pull request.
release-pr:
	git fetch --tags origin
	git worktree add .worktrees/release -b release origin/main
	git tag --list "v*" > .worktrees/release-tags.txt
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/release-pr.ps1 -Worktree .worktrees/release -TagsFile .worktrees/release-tags.txt -MessageFile .worktrees/release-message.txt
	git -C .worktrees/release commit --quiet --all --file ../release-message.txt
	git -C .worktrees/release push --quiet --set-upstream origin release
	gh pr create --base main --head release --fill
	@echo "Release pull request opened. Merging it publishes the release."

release:
	gh workflow run release.yml --ref main
	@echo "Release started on GitHub: gh run watch, or the Actions tab."

clean:
	$(call RUN_SCRIPT,clean)
