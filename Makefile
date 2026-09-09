TFM      := netstandard2.1
CONFIG   := Debug
DLL      := VGStockpile.dll

BUILDDIR := VGStockpile/bin/$(CONFIG)/$(TFM)
BUILDDLL := $(BUILDDIR)/$(DLL)

GAME_DIR   := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
PLUGIN_DIR := $(GAME_DIR)/BepInEx/plugins
VGSTOCKPILE_DIR := $(PLUGIN_DIR)/VGStockpile

PUBLICIZER ?= assembly-publicizer

DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)

export DOTNET_ROLL_FORWARD := LatestMajor

VGAPI_DLL ?= ../vanguard-galaxy-api/VGModAPI.Abstractions/bin/Release/netstandard2.1/VGModAPI.Abstractions.dll

.PHONY: all build link-asm refresh-asm link-api deploy clean test package

all: build

link-asm:
	@test -f VGStockpile/lib/Assembly-CSharp.dll || { echo 'Run make refresh-asm with owner-installed game references.'; exit 1; }
	@mkdir -p VGStockpile/lib
	ln -sfn "$(GAME_DIR)/VanguardGalaxy_Data/Managed/UnityEngine.UI.dll" VGStockpile/lib/UnityEngine.UI.dll
	ln -sfn "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Unity.TextMeshPro.dll" VGStockpile/lib/Unity.TextMeshPro.dll

refresh-asm:
	mkdir -p .local-reference
	$(PUBLICIZER) --strip "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" -o .local-reference/
	@test -s .local-reference/Assembly-CSharp-publicized.dll
	@mkdir -p VGStockpile/lib
	ln -sfn "$(CURDIR)/.local-reference/Assembly-CSharp-publicized.dll" VGStockpile/lib/Assembly-CSharp.dll

link-api:
	@test -f "$(VGAPI_DLL)" || { echo 'Build the sibling API Release package first.'; exit 1; }
	@mkdir -p VGStockpile/lib
	ln -sf "$$(realpath "$(VGAPI_DLL)")" VGStockpile/lib/VGModAPI.Abstractions.dll

build: link-asm link-api
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) build VGStockpile/VGStockpile.csproj -c $(CONFIG)

test: link-asm link-api
	python3 tools/test_package.py
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) test VGStockpile.Tests/VGStockpile.Tests.csproj -c $(CONFIG)

package: build
	python3 tools/package.py --configuration $(CONFIG)

deploy: package
	@test -d "$(PLUGIN_DIR)" || { echo "BepInEx plugins dir not found at $(PLUGIN_DIR)" ; exit 1 ; }
	@mkdir -p "$(VGSTOCKPILE_DIR)"
	cp "$(BUILDDIR)/VGStockpile.dll" "$(BUILDDIR)/Newtonsoft.Json.dll" "$(VGSTOCKPILE_DIR)/"
	@if [ -f "$(BUILDDIR)/VGStockpile.pdb" ]; then cp "$(BUILDDIR)/VGStockpile.pdb" "$(VGSTOCKPILE_DIR)/"; fi
	@echo "Deployed 2 DLL(s) to $(VGSTOCKPILE_DIR)"

clean:
	-$(DOTNET) clean VGStockpile/VGStockpile.csproj
	rm -rf VGStockpile/bin VGStockpile/obj VGStockpile.Tests/bin VGStockpile.Tests/obj dist/
