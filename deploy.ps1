$ErrorActionPreference = 'Stop'
$proj = "E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj"
$out  = "E:\DEV\PhoenixPoint\Multipleer\bin\Release"
$dest = "D:\Steam\steamapps\common\Phoenix Point\Mods\Multipleer"
dotnet build $proj -c Release
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$out\Multipleer.dll" $dest -Force
if (Test-Path "$out\Multipleer.pdb") { Copy-Item "$out\Multipleer.pdb" $dest -Force }
Copy-Item "E:\DEV\PhoenixPoint\Multipleer\meta.json" $dest -Force
$assets = "E:\DEV\PhoenixPoint\Multipleer\Assets"
if (Test-Path $assets) { Copy-Item $assets $dest -Recurse -Force }
Write-Host "Deployed Multipleer to $dest"
