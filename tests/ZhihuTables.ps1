param([string]$Document)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = New-Item -Force -ItemType Directory (Join-Path $root 'artifacts/ZhihuTables')
$vendor = Join-Path $root 'vendor/webview2'
$src = Join-Path $root 'src'
foreach ($name in 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll') {
    Copy-Item -LiteralPath (Join-Path $vendor $name) -Destination $out.FullName -Force
}
$exe = Join-Path $out.FullName 'TableCheck.exe'
$refs = @('System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','System.IO.Compression.dll',
    'System.Xml.Linq.dll','Microsoft.CSharp.dll',(Join-Path $vendor 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $vendor 'Microsoft.Web.WebView2.WinForms.dll')) | ForEach-Object { "/reference:$_" }
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /platform:x64 "/out:$exe" "/resource:$src\inspect.js,inspect.js" @refs "$src\Publish.cs" "$PSScriptRoot\ZhihuTables.cs"
if ($LASTEXITCODE -ne 0) { throw '回归测试编译失败' }
if ($Document) { & $exe $Document } else { & $exe }
exit $LASTEXITCODE
