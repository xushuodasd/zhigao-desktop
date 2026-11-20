param([string]$Document,[string]$DraftUrl,[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=(New-Item -Force -ItemType Directory (Join-Path $root 'artifacts/ZhihuDraftReadback')).FullName
$vendor=Join-Path $root 'vendor/webview2'
foreach ($name in 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll') {
    Copy-Item -LiteralPath (Join-Path $vendor $name) -Destination $out -Force
}
$refs=@('System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.Linq.dll','System.Web.Extensions.dll',
    (Join-Path $vendor 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $vendor 'Microsoft.Web.WebView2.WinForms.dll')) |
    ForEach-Object { "/reference:$_" }
$exe=Join-Path $out 'ZhihuDraftReadback.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /platform:x64 "/out:$exe" @refs "$PSScriptRoot\ZhihuDraftReadback.cs"
if($LASTEXITCODE -ne 0){ throw '草稿回读工具编译失败' }
if($BuildOnly){return}
if(!$Document -or !$DraftUrl){throw '请指定原 Word 文件和已有知乎草稿的完整编辑地址'}
& $exe (Resolve-Path -LiteralPath $Document).Path $DraftUrl
exit $LASTEXITCODE
