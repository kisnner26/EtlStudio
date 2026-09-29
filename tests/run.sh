#!/usr/bin/env bash
# Compila y ejecuta las pruebas del nucleo con Mono (macOS/Linux). No necesita SQL Server ni SSIS.
set -euo pipefail
cd "$(dirname "$0")/.."
out=tests/bin
mkdir -p "$out"
mcs -nowarn:1591 -out:"$out/EtlStudio.Tests.exe" \
  -r:System.dll -r:System.Core.dll -r:System.Data.dll -r:System.Security.dll -r:System.Web.Extensions.dll -r:System.Xml.dll -r:System.Xml.Linq.dll \
  tests/CoreTests.cs src/Core/Spec.cs src/Core/DdlGenerator.cs src/Core/Verification.cs \
  src/Core/Reconciliation.cs src/Core/Tools.cs src/Core/Plan.cs \
  src/Core/AppLog.cs src/Core/Notifier.cs src/Core/PackageInspector.cs src/Core/Requirements.cs
mono "$out/EtlStudio.Tests.exe"
