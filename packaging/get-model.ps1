# Downloads the DPDFNet noise-removal model that OpenKrisp needs. It is not bundled with the app:
# its license has not been checked for redistribution. Source: https://huggingface.co/Ceva-IP/DPDFNet
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'OpenKrisp\models'
$dst = Join-Path $dir 'dpdfnet2_48khz_hr.onnx'
$url = 'https://huggingface.co/Ceva-IP/DPDFNet/resolve/main/onnx/dpdfnet2_48khz_hr.onnx?download=true'
New-Item -ItemType Directory -Force $dir | Out-Null
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing
$size = (Get-Item $dst).Length
if ($size -lt 1MB) { Remove-Item $dst; throw "Download looks wrong ($size bytes)." }
Write-Host "Saved $dst ($([math]::Round($size / 1MB, 1)) MB)"
