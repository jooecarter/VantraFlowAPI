#!/usr/bin/env bash
# Parse every SQL batch the API sends and every db/*.sql migration with
# Microsoft's T-SQL parser (no database needed). See tools/sqlcheck/README.md.
#
#   scripts/sqlcheck.sh [--warnings] [--verbose] [--dump <dir>] [--file <x.sql>]
set -euo pipefail
cd "$(dirname "$0")/.."
# Build first: the checker reads the API's static SQL from the built assembly.
if ! out=$(dotnet build tools/sqlcheck -c Release -nologo 2>&1); then echo "$out"; exit 2; fi
exec dotnet run --project tools/sqlcheck -c Release --no-build -- "$@"
