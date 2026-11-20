param(
    [ValidateSet('App', 'Offline', 'LiveDraft', 'CsdnSettings', 'ZhihuManualPublish', 'CreatorPlatforms', 'SohuCreatorPublish')]
    [string]$Target = 'App'
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw "找不到 .NET Framework x64 C# 编译器：$compiler" }
$src = Join-Path $PSScriptRoot 'src'
$vendor = Join-Path $PSScriptRoot 'vendor\webview2'
$out = Join-Path $PSScriptRoot 'bin'
$name = '知稿.exe'
$sources = @((Join-Path $src 'Publish.cs'))
$entry = @()
if ($Target -ne 'Offline') { $sources += (Get-ChildItem -LiteralPath $src -Filter '*.cs' | Where-Object Name -ne 'Publish.cs').FullName }
if ($Target -ne 'App') {
    $out = Join-Path $PSScriptRoot "artifacts\$Target"
    $name = "$Target.exe"
    $sources += Join-Path $PSScriptRoot "tests\$Target.cs"
}
if ($Target -eq 'CsdnSettings') { $entry = @('/main:CsdnSettingsCheck') }
if ($Target -eq 'ZhihuManualPublish') { $entry = @('/main:ZhihuManualPublishCheck') }
if ($Target -eq 'CreatorPlatforms') { $entry = @('/main:CreatorPlatformsCheck') }
if ($Target -eq 'SohuCreatorPublish') { $entry = @('/main:SohuCreatorPublishCheck') }
if ($Target -eq 'LiveDraft') { $entry = @('/main:LiveCheck') }
New-Item -Force -ItemType Directory $out | Out-Null
$refs = @('System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.Xml.Linq.dll','Microsoft.CSharp.dll')
foreach ($dll in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
    $path = Join-Path $vendor $dll
    $targetDll = Join-Path $out $dll
    if (!(Test-Path -LiteralPath $targetDll) -or (Get-FileHash -LiteralPath $path).Hash -ne (Get-FileHash -LiteralPath $targetDll).Hash) {
        Copy-Item -LiteralPath $path -Destination $out -Force
    }
    if ($dll -ne 'WebView2Loader.dll') { $refs += $path }
}
$refArgs = $refs | ForEach-Object { "/reference:$_" }
$exe = Join-Path $out $name
$applicationType = if ($Target -in 'ZhihuManualPublish','CreatorPlatforms','SohuCreatorPublish') { 'exe' } else { 'winexe' }
$resources=@("/resource:$src\inspect.js,inspect.js","/resource:$src\dashboard.html,dashboard.html")
if($Target -ne 'Offline') { $resources += "/resource:$src\creator-editor.js,creator-editor.js" }
& $compiler /nologo /codepage:65001 "/target:$applicationType" /platform:x64 "/out:$exe" "/win32manifest:$src\app.manifest" "/win32icon:$src\zhigao.ico" @resources @entry @refArgs @sources
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
Write-Host "已生成 $exe"











