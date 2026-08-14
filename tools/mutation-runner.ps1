[CmdletBinding()]
param(
    [ValidateRange(1, 10000)]
    [int]$StartRegistration = 1,

    [ValidateRange(0, 10000)]
    [int]$Limit = 0,

    [string]$Managed = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("PhoenixpointMultiplayer-mutation-" + [Guid]::NewGuid().ToString("N"))
$markerPrefix = "INDIVIDUAL-LAW-MUTATION-"

function Copy-RepositoryFiles {
    param([string]$Destination)

    New-Item -ItemType Directory -Path $Destination | Out-Null
    $relativeFiles = @(& git -C $repoRoot ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $relativeFiles.Count -eq 0) {
        throw "Could not enumerate repository files."
    }

    foreach ($relative in $relativeFiles) {
        $source = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $source -Destination $target
    }
}

try {
    Copy-RepositoryFiles $scratch

    $dotnet = Join-Path $env:ProgramFiles "dotnet/dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = "dotnet" }

    $programPath = Join-Path $scratch "tools/RailCheck/Program.cs"
    $source = [IO.File]::ReadAllText($programPath)
    $registrationCount = [regex]::Matches(
        $source,
        '(?m)^\s*Add\(laws, \(\) => .+\);\s*$'
    ).Count
    if ($registrationCount -le 0) { throw "No law registrations found in Program.cs." }

    $counterNeedle = '            _lawsRegistered++;'
    $counterReplacement = @'
            _lawsRegistered++;
            var mutationRegistration = _lawsRegistered;
'@
    if (($source.Split($counterNeedle).Count - 1) -ne 1) {
        throw "The Add registration counter seam is missing or ambiguous."
    }
    $source = $source.Replace($counterNeedle, $counterReplacement.TrimEnd("`r", "`n"))

    $drainNeedle = '                foreach (var v in result) laws.Add(v);'
    $drainReplacement = @'
                foreach (var v in result) laws.Add(v);
                int selectedMutation;
                if (int.TryParse(Environment.GetEnvironmentVariable("RAILCHECK_MUTATION_REGISTRATION"),
                                 out selectedMutation) && selectedMutation == mutationRegistration)
                    laws.Add("INDIVIDUAL-LAW-MUTATION-" + mutationRegistration);
'@
    if (($source.Split($drainNeedle).Count - 1) -ne 1) {
        throw "The Add result-drain seam is missing or ambiguous."
    }
    $source = $source.Replace($drainNeedle, $drainReplacement.TrimEnd("`r", "`n"))
    [IO.File]::WriteAllText($programPath, $source, [Text.UTF8Encoding]::new($false))

    & $dotnet build (Join-Path $scratch "tools/RailCheck/RailCheck.csproj") -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Instrumented RailCheck build failed." }

    $railCheck = Join-Path $scratch "tools/RailCheck/bin/Debug/RailCheck.exe"
    $arguments = @()
    if ($Managed) { $arguments += @("--managed", $Managed) }

    Remove-Item Env:RAILCHECK_MUTATION_REGISTRATION -ErrorAction SilentlyContinue
    $baselineOutput = @(& $railCheck @arguments 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not ($baselineOutput -match 'RAILCHECK GREEN')) {
        throw "Unmodified baseline did not pass in the temporary copy:`n$($baselineOutput -join "`n")"
    }

    if ($StartRegistration -gt $registrationCount) {
        throw "StartRegistration $StartRegistration exceeds registration count $registrationCount."
    }
    $lastRegistration = if ($Limit -eq 0) {
        $registrationCount
    } else {
        [Math]::Min($StartRegistration + $Limit - 1, $registrationCount)
    }
    $runCount = $lastRegistration - $StartRegistration + 1
    $failures = [Collections.Generic.List[string]]::new()
    for ($registration = $StartRegistration; $registration -le $lastRegistration; $registration++) {
        $env:RAILCHECK_MUTATION_REGISTRATION = $registration.ToString([Globalization.CultureInfo]::InvariantCulture)
        $output = @(& $railCheck @arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $marker = $markerPrefix + $registration
        if ($exitCode -ne 1 -or -not ($output -match [regex]::Escape($marker))) {
            $failures.Add("registration ${registration}: exit=$exitCode marker=$($output -match [regex]::Escape($marker))")
        }
        $completed = $registration - $StartRegistration + 1
        if (($completed % 25) -eq 0 -or $registration -eq $lastRegistration) {
            Write-Host "Mutation progress: $completed/$runCount (registrations $StartRegistration-$lastRegistration)"
        }
    }

    if ($failures.Count -ne 0) {
        Write-Error ("MUTATION RED: {0}/{1} individual mutations escaped.`n{2}" -f
            $failures.Count, $runCount, ($failures -join "`n"))
        exit 1
    }

    Write-Host "MUTATION GREEN: $runCount/$runCount registered laws independently forced a red verdict."
}
finally {
    Remove-Item Env:RAILCHECK_MUTATION_REGISTRATION -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
