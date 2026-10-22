$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$destination = Join-Path (Split-Path $PSScriptRoot -Parent) '校园网自动连接.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$destination" "/win32manifest:$PSScriptRoot\app.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll "$PSScriptRoot\CampusAutoLogin.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output $destination
