# PuppyMacro commands. They run on Windows (GNU make, see .claude/rules/build-and-release.md) and in WSL, where they
# use the Windows .NET SDK through Windows PowerShell. Options are make variables, lowercase: make dev keep=1.
#
#   make dev        build the current code and start it with fresh sample data (development build);
#                   make dev keep=1 keeps the development build's data from the last run
#   make test       run the unit tests
#   make e2e        run the end-to-end tests on the development build (do not use the PC meanwhile);
#                   make e2e category=Macros runs one category (UI, Macros, Loops, Remaps, Recording, Overlay, AI)
#   make build      create the distribution in dist/ (Setup.exe + update packages), no upload
#   make docs       build the website in site-preview/ with the guide of the current code;
#                   make docs serve=1 also serves it at http://localhost:8080/ (Ctrl+C to stop)
#   make file-icons draw the icons of .puppyitems and .puppymacro files again (PuppyMacro/Assets/FileIcons);
#                   make file-icons preview=<folder> also writes each size as a PNG there
#   make clean      remove build output
#
# Releases are not made here: the create_release_pr workflow opens the release pull request, merging it publishes the
# release (publish_release). See build-and-release.md.

ifeq ($(OS),Windows_NT)
POWERSHELL := powershell
else
POWERSHELL := powershell.exe
# WSL hands only the variables listed in WSLENV to Windows programs: the code signing ones (make build).
export WSLENV := CERTUM_EMAIL:CERTUM_OTP:CERTUM_TOKEN$(if $(WSLENV),:$(WSLENV))
endif

RUN_SCRIPT = $(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/$(1).ps1

.PHONY: dev test e2e build docs file-icons clean

dev:
	$(call RUN_SCRIPT,dev) $(if $(keep),-Keep)

test:
	$(call RUN_SCRIPT,test)

e2e:
	$(call RUN_SCRIPT,e2e) $(if $(category),-Category $(category))

build:
	$(call RUN_SCRIPT,build)

docs:
ifdef serve
	$(call RUN_SCRIPT,site-serve)
else
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/site.ps1 -Preview
endif

file-icons:
	$(call RUN_SCRIPT,file-icons) $(if $(preview),-Preview '$(preview)')

clean:
	$(call RUN_SCRIPT,clean)
