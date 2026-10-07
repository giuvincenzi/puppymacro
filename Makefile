# PuppyMacro commands. They run on Windows (GNU make, see README) and in WSL, where they
# use the Windows .NET SDK through Windows PowerShell.
#
#   make dev        build the current code and start it with fresh sample data (development build)
#   make dev-keep   same, keeping the development build's data from the last run
#   make test       run the unit tests
#   make build      create the distribution in dist/ (Setup.exe + update packages), no upload
#   make docs       build the website in site-preview/ with the guide of the current code
#   make release    publish the version in PuppyMacro.csproj to GitHub Releases (runs the Release workflow)
#   make clean      remove build output

ifeq ($(OS),Windows_NT)
POWERSHELL := powershell
else
POWERSHELL := powershell.exe
endif

RUN_SCRIPT = $(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/$(1).ps1

.PHONY: dev dev-keep test build docs release clean

dev:
	$(call RUN_SCRIPT,dev)

dev-keep:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Keep

test:
	$(call RUN_SCRIPT,test)

build:
	$(call RUN_SCRIPT,build)

docs:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/site.ps1 -Preview

release:
	gh workflow run release.yml --ref main
	@echo "Release started on GitHub: gh run watch, or the Actions tab."

clean:
	$(call RUN_SCRIPT,clean)
