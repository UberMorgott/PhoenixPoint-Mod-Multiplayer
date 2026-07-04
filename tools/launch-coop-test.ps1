#requires -Version 7.0
<#
.SYNOPSIS
    Launch TWO windowed Phoenix Point instances side-by-side for local Multiplayer co-op testing.

.DESCRIPTION
    Instance #1 = HOST (uses the real Multiplayer\identity.json).
    Instance #2 = CLIENT (gets a throwaway GUID via the MULTIPLAYER_IDENTITY env-var seam,
    set ONLY for that child process so the two instances do not share a player identity).

    Steam client MUST already be running. This script does not start Steam.
#>
param(
    [int]$Width  = 960,   # half of a 1920-wide screen
    [int]$Height = 1080
)

$ErrorActionPreference = 'Stop'

$game = "D:\Steam\steamapps\common\Phoenix Point"
$exe  = "$game\PhoenixPointWin64.exe"

# --- Validate install ---------------------------------------------------------
if (-not (Test-Path -LiteralPath $game)) {
    throw "Game folder not found: $game"
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Game exe not found: $exe"
}

# --- Ensure steam_appid.txt ---------------------------------------------------
# Lets the exe init Steamworks directly (no Steam relauncher), which is what allows
# a 2nd instance to come up under the same Steam session. Created only if missing.
$appIdFile = "$game\steam_appid.txt"
if (-not (Test-Path -LiteralPath $appIdFile)) {
    Set-Content -LiteralPath $appIdFile -Value "839770" -NoNewline -Encoding ascii
    Write-Host "Created $appIdFile (839770) - enables Steamworks init without the Steam relauncher."
} else {
    Write-Host "Found existing $appIdFile (left as-is)."
}

# --- Common window args -------------------------------------------------------
$winArgs = @(
    '-screen-fullscreen', '0',
    '-popupwindow',
    '-screen-width',  "$Width",
    '-screen-height', "$Height"
)

Write-Host ""
Write-Host "Launching two windowed instances ($Width x $Height each)..."

# --- INSTANCE #1: HOST (real identity, no override) ---------------------------
Start-Process -FilePath $exe -ArgumentList $winArgs -WorkingDirectory $game
Write-Host "  [#1 HOST]   launched (real identity.json)."

# --- INSTANCE #2: CLIENT (throwaway identity via env-var seam) ----------------
$clientGuid = [guid]::NewGuid().ToString()
Start-Process -FilePath $exe -ArgumentList $winArgs -WorkingDirectory $game `
    -Environment @{ MULTIPLAYER_IDENTITY = $clientGuid }
Write-Host "  [#2 CLIENT] launched with MULTIPLAYER_IDENTITY=$clientGuid"

# --- Manual post-launch steps -------------------------------------------------
Write-Host ""
Write-Host "Next steps (manual):"
Write-Host "  Prereq : Steam client must be running."
Write-Host "  Host   : in instance #1, create or load a campaign -> it starts hosting on :14242."
Write-Host "  Client : in instance #2, Multiplayer -> Direct Connect -> 127.0.0.1:14242"
Write-Host "  Layout : drag/tile the two windows side-by-side (Win+Left / Win+Right)."
Write-Host ""
Write-Host "If instance #2 will not start: confirm Steam is open and steam_appid.txt is in the game folder."
