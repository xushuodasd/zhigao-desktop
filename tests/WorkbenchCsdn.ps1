param([Parameter(Mandatory=$true)][string]$Document)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=(New-Item -Force -ItemType Directory (Join-Path $root 'artifacts/WorkbenchCsdn')).FullName
$src=Join-Path $root 'src'
$vendor=Join-Path $root 'vendor/webview2'
foreach ($name in 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll') {
    Copy-Item -LiteralPath (Join-Path $vendor $name) -Destination $out -Force
}
$refs=@('System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.Xml.Linq.dll',
    'Microsoft.CSharp.dll',(Join-Path $vendor 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $vendor 'Microsoft.Web.WebView2.WinForms.dll')) |
    ForEach-Object { "/reference:$_" }
$sources=(Get-ChildItem -LiteralPath $src -Filter '*.cs').FullName
$exe=Join-Path $out 'WorkbenchCsdn.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /platform:x64 /main:WorkbenchCsdnCheck "/out:$exe" "/resource:$src\dashboard.html,dashboard.html" "/resource:$src\inspect.js,inspect.js" "/resource:$src\creator-editor.js,creator-editor.js" @refs @sources "$PSScriptRoot\WorkbenchCsdn.cs"
if($LASTEXITCODE -ne 0) { throw 'CSDN 工作台回归编译失败' }
& $exe (Resolve-Path -LiteralPath $Document).Path
exit $LASTEXITCODE
