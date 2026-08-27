Set-StrictMode -Version Latest

function Resolve-CetusDotNet {
    [CmdletBinding()]
    param()

    $candidates = @(
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
        "C:\Program Files\dotnet\dotnet.exe"
    )

    $pathCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($pathCommand) {
        $candidates += $pathCommand.Source
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks -match '^10\.') {
            return $candidate
        }
    }

    throw ".NET 10 SDK was not found. Install it or place dotnet.exe in a standard location."
}
