$ErrorActionPreference = 'Stop'

function Invoke-Checked([string]$command, [string[]]$arguments) {
    & $command @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$command $($arguments -join ' ') failed (exit $LASTEXITCODE)."
    }
}

$originalPath = $env:PATH
$originalCargoHome = $env:CARGO_HOME
$originalRustupHome = $env:RUSTUP_HOME
Push-Location (Join-Path $PSScriptRoot 'engine')
try {
    if (-not (Get-Command cargo -CommandType Application -ErrorAction SilentlyContinue)) {
        $localRust = Join-Path $env:LOCALAPPDATA 'NtfsJournalBuild'
        $cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $localRust 'cargo' }
        $cargoBin = Join-Path $cargoHome 'bin'
        if (-not (Test-Path -LiteralPath (Join-Path $cargoBin 'cargo.exe'))) {
            throw 'Install Rust with rustfmt and clippy, then run this check from a Windows build shell.'
        }
        $env:PATH = "$cargoBin;$env:PATH"
        $env:CARGO_HOME = $cargoHome
        if (-not $env:RUSTUP_HOME -and $cargoHome -eq (Join-Path $localRust 'cargo')) {
            $env:RUSTUP_HOME = Join-Path $localRust 'rustup'
        }
    }

    Invoke-Checked cargo @('fmt', '--', '--check')
    Invoke-Checked cargo @('clippy', '--locked', '--all-targets', '--', '-D', 'warnings')
    $artifacts = Invoke-Checked cargo @('build', '--release', '--locked', '--message-format=json-render-diagnostics')
    $engine = $artifacts | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.target.name -eq 'ntfstracer-cli' -and $_.executable } |
        Select-Object -ExpandProperty executable -Last 1
    if (-not $engine -or -not (Test-Path -LiteralPath $engine -PathType Leaf)) {
        throw 'Cargo did not produce the ntfstracer-cli executable.'
    }

    Set-Location $PSScriptRoot
    Invoke-Checked dotnet @('restore', 'NtfsTracer.slnx', '--verbosity', 'quiet')
    Invoke-Checked dotnet @('format', 'whitespace', 'NtfsTracer.slnx', '--no-restore', '--verify-no-changes')
    Invoke-Checked dotnet @('build', 'NtfsTracer.slnx', '-c', 'Release', '--no-restore', '--no-incremental', '-warnaserror',
        '-p:AnalysisLevel=10.0-recommended', "-p:JournalIndexPath=$engine")
    Invoke-Checked dotnet @('run', '--no-build', '-c', 'Release', '--project', 'tests/LanguageCoverage', '--', 'gui')
}
finally {
    Pop-Location
    $env:PATH = $originalPath
    $env:CARGO_HOME = $originalCargoHome
    $env:RUSTUP_HOME = $originalRustupHome
}
