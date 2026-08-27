#!/usr/bin/env bash
# Run CETUS pre-package checks without sharing user-facing state.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
dev_root="$repo_root/.dev-check"
port="${CETUS_DEV_PORT:-3084}"
check_only=false
reset=false

usage() {
  cat <<'USAGE'
Usage: bash scripts/dev-check.sh [--check-only] [--reset]

Runs Debug tests, then launches CETUS against isolated development state.

Options:
  --check-only  Run the Debug test suite without launching the desktop app.
  --reset       Delete only the isolated .dev-check state before running.
  --help        Show this help text.

Override the isolated port with CETUS_DEV_PORT. The default is 3084.
USAGE
}

for argument in "$@"; do
  case "$argument" in
    --check-only) check_only=true ;;
    --reset) reset=true ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$argument" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$port" =~ ^[1-9][0-9]{0,4}$ ]] || (( port > 65535 )); then
  printf 'CETUS_DEV_PORT must be an integer from 1 through 65535.\n' >&2
  exit 2
fi

for command in wslpath powershell.exe; do
  if ! command -v "$command" >/dev/null 2>&1; then
    printf 'Required command is unavailable: %s\n' "$command" >&2
    exit 1
  fi
done

if [[ "$reset" == true ]]; then
  rm -rf "$dev_root"
fi

mkdir -p "$dev_root/dsh-home" "$dev_root/webview2" "$dev_root/logs"

# Use the same version-pinned runtime the packaging pipeline ships so local
# checks exercise the exact CLI and dependency graph included in releases.
runtime_node="$repo_root/dist/runtime/node.exe"
runtime_entry="$repo_root/dist/runtime/dsh/node_modules/@deepseek-ai/dsh/lib/bin.js"
if [[ ! -f "$runtime_node" || ! -f "$runtime_entry" ]]; then
  printf 'Pinned runtime is missing under dist/runtime.\n' >&2
  printf 'Run scripts/publish.ps1 once (or restore dist/runtime) before dev-check.\n' >&2
  exit 1
fi

windows_path() {
  wslpath -w "$1"
}

export CETUS_DEV_ROOT="$(windows_path "$repo_root")"
export CETUS_DEV="1"
export CETUS_PORT="$port"
export CETUS_INSTANCE_ID="dev-check-$$"
export CETUS_NODE_EXE="$(windows_path "$runtime_node")"
export CETUS_DSH_ENTRY="$(windows_path "$runtime_entry")"
export CETUS_DSH_HOME="$(windows_path "$dev_root/dsh-home")"
export CETUS_SETTINGS_PATH="$(windows_path "$dev_root/settings.json")"
export CETUS_WEBVIEW2_USER_DATA="$(windows_path "$dev_root/webview2")"
export CETUS_LOG_DIR="$(windows_path "$dev_root/logs")"
export DSH_HOME="$CETUS_DSH_HOME"

powershell_literal() {
  printf '%s' "$1" | sed "s/'/''/g"
}

run_powershell() {
  local script_file exit_code
  script_file="$dev_root/.dev-check-$$-$RANDOM.ps1"
  {
    printf "\$env:CETUS_DEV_ROOT = '%s'\n" "$(powershell_literal "$CETUS_DEV_ROOT")"
    printf "\$env:CETUS_PORT = '%s'\n" "$(powershell_literal "$CETUS_PORT")"
    printf "\$env:CETUS_DEV = '%s'\n" "$(powershell_literal "$CETUS_DEV")"
    printf "\$env:CETUS_INSTANCE_ID = '%s'\n" "$(powershell_literal "$CETUS_INSTANCE_ID")"
    printf "\$env:CETUS_NODE_EXE = '%s'\n" "$(powershell_literal "$CETUS_NODE_EXE")"
    printf "\$env:CETUS_DSH_ENTRY = '%s'\n" "$(powershell_literal "$CETUS_DSH_ENTRY")"
    printf "\$env:CETUS_DSH_HOME = '%s'\n" "$(powershell_literal "$CETUS_DSH_HOME")"
    printf "\$env:CETUS_SETTINGS_PATH = '%s'\n" "$(powershell_literal "$CETUS_SETTINGS_PATH")"
    printf "\$env:CETUS_WEBVIEW2_USER_DATA = '%s'\n" "$(powershell_literal "$CETUS_WEBVIEW2_USER_DATA")"
    printf "\$env:CETUS_LOG_DIR = '%s'\n" "$(powershell_literal "$CETUS_LOG_DIR")"
    printf "\$env:DSH_HOME = '%s'\n\n" "$(powershell_literal "$DSH_HOME")"
    cat
  } > "$script_file"
  exit_code=0
  powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$(windows_path "$script_file")" || exit_code=$?
  rm -f "$script_file"
  return "$exit_code"
}

printf 'CETUS isolated development check\n'
printf '  port       : %s\n' "$CETUS_PORT"
printf '  dsh home   : %s\n' "$CETUS_DSH_HOME"
printf '  settings   : %s\n' "$CETUS_SETTINGS_PATH"
printf '  webview2   : %s\n' "$CETUS_WEBVIEW2_USER_DATA"
printf '  logs       : %s\n' "$CETUS_LOG_DIR"

run_powershell <<'POWERSHELL'
$ErrorActionPreference = 'Stop'
. "$env:CETUS_DEV_ROOT\scripts\common.ps1"
$dotnet = Resolve-CetusDotNet

& $dotnet test "$env:CETUS_DEV_ROOT\Cetus.slnx" -c Debug -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
POWERSHELL

if [[ "$check_only" == true ]]; then
  printf 'Debug tests passed. Desktop launch skipped.\n'
  exit 0
fi

run_powershell <<'POWERSHELL'
$ErrorActionPreference = 'Stop'
$port = [int]$env:CETUS_PORT
if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
  Write-Error "The isolated development port $port is already listening. Choose another CETUS_DEV_PORT; do not reuse an existing DSH service."
  exit 1
}

. "$env:CETUS_DEV_ROOT\scripts\common.ps1"
$dotnet = Resolve-CetusDotNet

& $dotnet run --no-build --project "$env:CETUS_DEV_ROOT\src\Cetus.Desktop\Cetus.Desktop.csproj" -c Debug
exit $LASTEXITCODE
POWERSHELL
