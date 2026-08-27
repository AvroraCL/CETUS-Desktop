#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dev_script="$(wslpath -w "$script_dir/dev.ps1")"

command="check"
arguments=()
for argument in "$@"; do
  case "$argument" in
    --check-only) command="test" ;;
    --reset) command="reset" ;;
    --help|-h)
      printf 'Usage: bash scripts/dev-check.sh [--check-only|--reset]\n'
      printf 'All implementation lives in scripts/dev.ps1. Default command: check.\n'
      exit 0
      ;;
    *) arguments+=("$argument") ;;
  esac
done

pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "$dev_script" "$command" "${arguments[@]}"
