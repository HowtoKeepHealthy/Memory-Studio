$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
$taskZip = Join-Path $taskTools 'nasm-2.16.03-win64.zip'
$taskHash = '3EE4782247BCB874378D02F7EAB4E294A84D3D15F3F6EE2DE2F47A46AA7226E6'
$taskNasm = Join-Path $taskTools 'nasm\nasm-2.16.03\nasm.exe'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskZip)) {
    Invoke-WebRequest -Uri 'https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/win64/nasm-2.16.03-win64.zip' -OutFile $taskZip -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash -ne $taskHash) { throw 'NASM archive hash mismatch.' }
if (-not (Test-Path -LiteralPath $taskNasm)) { Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskTools 'nasm') -Force }
$taskNative = Join-Path $taskRoot 'artifacts\native'
New-Item -ItemType Directory -Path $taskNative -Force | Out-Null
Copy-Item -LiteralPath $taskNasm -Destination (Join-Path $taskNative 'nasm.exe') -Force
Copy-Item -LiteralPath (Join-Path $taskTools 'nasm\nasm-2.16.03\LICENSE') -Destination (Join-Path $taskNative 'NASM-LICENSE.txt') -Force
