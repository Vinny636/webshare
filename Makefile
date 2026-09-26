.DEFAULT_GOAL := help

CONFIGURATION ?= Release
TARGET_FRAMEWORK := net10.0
LINUX_RID := linux-x64
WINDOWS_RID := win-x64
LINUX_PUBLISH_DIR := bin/$(CONFIGURATION)/$(TARGET_FRAMEWORK)/$(LINUX_RID)/publish
WINDOWS_PUBLISH_DIR := bin/$(CONFIGURATION)/$(TARGET_FRAMEWORK)/$(WINDOWS_RID)/publish
PREFIX ?= /usr/local
BINDIR ?= $(PREFIX)/bin
DESTDIR ?=

ifeq ($(OS),Windows_NT)
HOST_PUBLISH_TARGET := publish-windows
else
HOST_PUBLISH_TARGET := publish-linux
endif

.PHONY: help build publish publish-linux publish-windows install clean

help:
	@echo "Targets:"
	@echo "  build            Build the project"
	@echo "  publish          Publish for the current platform"
	@echo "  publish-linux    Publish a self-contained Linux x64 binary"
	@echo "  publish-windows  Publish a self-contained Windows x64 executable"
	@echo "  install          Publish and install for the current platform"
	@echo "  clean            Clean build outputs"

build:
	dotnet build -c $(CONFIGURATION)

publish: $(HOST_PUBLISH_TARGET)

publish-linux:
	dotnet publish -c $(CONFIGURATION) -r $(LINUX_RID)

publish-windows:
	dotnet publish -c $(CONFIGURATION) -r $(WINDOWS_RID)

ifeq ($(OS),Windows_NT)
install: publish
	powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/install-windows.ps1 -Source "$(CURDIR)/$(WINDOWS_PUBLISH_DIR)/webshare.exe" -InstallDirectory "$(CURDIR)"
else
install: publish
	@if [ -w "$(DESTDIR)$(BINDIR)" ]; then \
		install -Dm755 "$(LINUX_PUBLISH_DIR)/webshare" "$(DESTDIR)$(BINDIR)/webshare"; \
	else \
		sudo install -Dm755 "$(LINUX_PUBLISH_DIR)/webshare" "$(DESTDIR)$(BINDIR)/webshare"; \
	fi
endif

clean:
	dotnet clean -c $(CONFIGURATION)
