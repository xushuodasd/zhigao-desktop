# 知稿

Windows 11 x64 上的知乎和 CSDN 文章发布客户端。在「账号」中登录平台，再回到工作台，拖入 Word、勾选账号，点「发布文章」。软件以文件名作标题，各账号分别显示进度、结果及已确认的文章链接。仅勾选知乎时，可以点击「存知乎草稿」。知稿重新打开已保存的草稿，核对标题、图文、表格和已选话题，在记录中提供「打开草稿」。CSDN 遇到临时问题会后台核查并按回执决定是否重试。

## 从源码运行

需要 Windows x64、.NET Framework 4.8 和 Microsoft Edge WebView2 Runtime。随项目附有 WebView2 SDK 1.0.3537.50 的三个 x64 构建／运行依赖及许可文件。正常构建使用 Windows 自带的 .NET Framework 编译器，在本地完成。

从项目根目录打开 PowerShell，先关闭正在该项目 bin 目录运行的知稿，然后执行：

```powershell
.\build.ps1
& '.\bin\知稿.exe'
```

如果个人电脑的执行策略限制本地脚本，可以在项目目录运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`。该参数仅对这一次 PowerShell 进程有效。

## 验证修改

**离线回归。** 使用本地模拟的知乎页面和独立 WebView2 配置，验证成功回执、必填设置、跳转到错误文章三个场景。结果写入 `artifacts\Offline\report.txt`。

```powershell
.\build.ps1 -Target Offline
$p = Start-Process '.\artifacts\Offline\Offline.exe' -Wait -PassThru
$p.ExitCode
Get-Content '.\artifacts\Offline\report.txt' -Encoding UTF8 -Tail 3
```

**真实草稿验证。** 下列命令会使用本机已登录的知乎账号，把指定稿件上传为知乎草稿。它运行正式版的导入与核对流程，在检查发布按钮后退出。示例中的路径要换成你愿意上传的 Word 文件。

```powershell
.\build.ps1 -Target LiveDraft
Start-Process '.\artifacts\LiveDraft\LiveDraft.exe' -ArgumentList '"C:\稿件\测试文章.docx"' -Wait
Get-Content '.\artifacts\LiveDraft\result.txt' -Encoding UTF8
```

## 项目入口

| 路径 | 用途 |
| --- | --- |
| `AGENTS.md` | AI 维护手册，记录真实故障、关键选择器、提交状态与回归要求 |
| `src/Login.cs` | 工作台、账号状态、拖入文件、发布记录 |
| `src/dashboard.html` | 内嵌的工作台界面，包含发布、账号、记录和操作动效 |
| `src/Publish.cs` | Word 解析、后台导入、图文核对、发布与去重 |
| `src/inspect.js` | 编译时嵌入程序的知乎页面适配代码 |
| `tests/` | 离线回执测试和真实草稿验证程序 |
| `vendor/webview2/` | 固定版本依赖与第三方许可 |

登录状态和发布记录位于本机 `%LOCALAPPDATA%\Zhigao`。源码目录可以移动。通过同一 Windows 用户打开客户端会继续使用原来的登录状态，直到知乎要求重新登录。



