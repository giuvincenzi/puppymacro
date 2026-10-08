# PuppyMacro commands. They run on Windows (GNU make, see .claude/rules/build-and-release.md) and in WSL, where they
# use the Windows .NET SDK through Windows PowerShell.
#
#   make dev        build the current code and start it with fresh sample data (development build)
#   make dev-keep   same, keeping the development build's data from the last run
#   make test       run the unit tests
#   make e2e        run the end-to-end tests on the development build (do not use the PC meanwhile)
#   make build      create the distribution in dist/ (Setup.exe + update packages), no upload
#   make docs       build the website in site-preview/ with the guide of the current code
#   make docs-serve same, then serve it at http://localhost:8080/ (Ctrl+C to stop)
#   make screenshots make dev, then retake the guide's editor screenshots and the home page one (drives the UI)
#   make release-pr open the release pull request (next version from the latest tag and CHANGELOG);
#                   merging it publishes the release
#   make release    retry the Release workflow on main (it never republishes an existing tag)
#   make clean      remove build output

ifeq ($(OS),Windows_NT)
POWERSHELL := powershell
else
POWERSHELL := powershell.exe
endif

RUN_SCRIPT = $(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/$(1).ps1

.PHONY: dev dev-keep test e2e build docs docs-serve screenshots release-pr release clean

dev:
	$(call RUN_SCRIPT,dev)

dev-keep:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Keep

test:
	$(call RUN_SCRIPT,test)

e2e:
	$(call RUN_SCRIPT,e2e)

build:
	$(call RUN_SCRIPT,build)

screenshots: dev
	$(call RUN_SCRIPT,screenshots)

docs:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/site.ps1 -Preview

docs-serve:
	$(call RUN_SCRIPT,site-serve)

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
