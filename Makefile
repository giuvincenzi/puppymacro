# PuppyMacro commands. They run on Windows (GNU make, see README) and in WSL, where they
# use the Windows .NET SDK through Windows PowerShell.
#
#   make dev        build the current code and start it with fresh sample data (development build)
#   make dev-keep   same, keeping the development build's data from the last run
#   make test       run the unit tests
#   make e2e        run the end-to-end tests on the development build (do not use the PC meanwhile)
#   make build      create the distribution in dist/ (Setup.exe + update packages), no upload
#   make docs       build the website in site-preview/ with the guide of the current code
#   make docs-serve same, then serve it at http://localhost:8080/ (Ctrl+C to stop)
#   make release    publish the version in PuppyMacro.csproj to GitHub Releases (runs the Release workflow)
#   make clean      remove build output

ifeq ($(OS),Windows_NT)
POWERSHELL := powershell
else
POWERSHELL := powershell.exe
endif

RUN_SCRIPT = $(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/$(1).ps1

.PHONY: dev dev-keep test e2e build docs docs-serve release clean

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

docs:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/site.ps1 -Preview

docs-serve:
	$(call RUN_SCRIPT,site-serve)

release:
	gh workflow run release.yml --ref main
	@echo "Release started on GitHub: gh run watch, or the Actions tab."

clean:
	$(call RUN_SCRIPT,clean)
