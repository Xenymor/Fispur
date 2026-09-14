# OpenBench build entry point.
#
# The OpenBench worker builds every branch it tests by running
#     make -j EXE=<name> CC=<compiler>
# in the directory named by the engine config's "build path" (empty = repo root),
# and afterwards moves exactly one file called <name>[.exe] out of it.
#
# That single-file contract is why this publishes self-contained: a framework
# dependent publish would leave the runtime DLLs behind and the moved binary
# would not start. OB_EXE (read by Fispur-cli-(exp).csproj) makes dotnet emit the
# file under the name OpenBench expects, so no copy step (and no shell utilities
# beyond dotnet itself) is needed - Windows workers only have make and dotnet.
# OB_EXE also switches off the GUI's asset copies and host artifacts in
# Fispur.csproj, so the publish drops exactly one file here and nothing else.
#
# CC/CXX are passed by OpenBench for C-like engines; they are ignored here.

EXE     ?= Fispur
PROJECT := Fispur-cli-(exp)/Fispur-cli-(exp).csproj

# Runtime identifier from the host OS, so the same Makefile serves Windows and
# Linux workers. On Linux the published file has no extension, which is exactly
# what OpenBench looks for there; on Windows it gets .exe. Override with RID=...
ifeq ($(OS),Windows_NT)
  RID ?= win-x64
else ifeq ($(shell uname -m),aarch64)
  RID ?= linux-arm64
else
  RID ?= linux-x64
endif

.PHONY: all clean
all:
	dotnet publish "$(PROJECT)" -c Release -r $(RID) --self-contained true \
	  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
	  -p:OB_EXE=$(EXE) -p:DebugType=none -o .

clean:
	dotnet clean "$(PROJECT)" -c Release
