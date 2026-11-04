# Chrome 独立环境启动器

## EXE 版（推荐）

从 [Releases](https://github.com/Ptowen/chrome-privacy-launcher/releases/latest) 下载 `ChromePrivacy.exe`，双击后点击「启动独立 Chrome」。单个文件即可运行，内置 Node.js，不需要手动安装运行环境；电脑需要已经安装 Chrome。适用于 Windows 10/11 x64，使用系统自带的 .NET Framework 4.x，无需管理员权限。

运行期间保持启动器窗口打开。点击「停止并关闭此环境」或关闭启动器会关闭它启动的 Chrome；已有的日常 Chrome 不受影响。首次启动会将内置资源释放到 `%LOCALAPPDATA%\ChromePrivacyLauncher\runtime`，该工具自己的登录和历史保存在 `%LOCALAPPDATA%\ChromePrivacyLauncher\chrome-profile`。不会迁移原脚本版本的数据。

EXE 版与脚本版功能相同：语言和默认区域格式为英语，网页时区为 UTC。没有配置代理或 WebRTC 直连限制，不解决截图中的网络地址暴露；字体、Emoji、IP 保持实际值。所有检测结论以实测为准。

内置资源包含 Node.js 官方完整许可证，释放后可在 runtime 版本目录查看 `NODE-LICENSE.txt`。源码在 `desktop/ChromePrivacy.cs`，用 `desktop/build.ps1` 构建。EXE 提供 `--self-test <项目内测试目录>` 用于无界面自检，结果写入指定目录的 `self-test.log`。

## 脚本版

双击 `Start.cmd`。自动寻找 Chrome 和 Node.js，启动独立 Chrome 并打开本地检测页。请保留控制台窗口；退出按 Ctrl+C，会一并关闭此工具启动的 Chrome。也可以直接关闭这些 Chrome 窗口。

脚本版需要 Chrome 和 Node.js 22.4+（建议当前 LTS）；不需要 npm 包。找不到 Chrome 时可手动选 `chrome.exe`；也可设置 `CHROME_BINARY` 环境变量。Node.js 下载：https://nodejs.org/ 。不自动下载安装，cmd 的 ExecutionPolicy Bypass 只对当次 PowerShell 有效。

## 从源码构建 EXE

在 Windows x64 上安装 Node.js v24.16.0，然后运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\desktop\build.ps1
```

也可以用 `-NodePath "路径\node.exe"` 指定该版本的 Node.js。构建使用 Windows 自带的 .NET Framework C# 编译器，不需要安装 .NET SDK。输出位于 `dist/ChromePrivacy.exe`。`desktop/NODE-LICENSE.txt` 包含此版本 Node.js 的第三方许可文本。

## 修改范围

- 使用工具目录内的 `chrome-profile` 独立数据目录，不读取或复制日常 Chrome 的账号、Cookie、扩展和历史。
- `--lang=en-US --accept-lang=en-US,en` 设置此 Chrome 的语言和默认区域格式；通过本机回环调试连接为普通页面应用 UTC 时区。具体效果以检测页为准。
- 对新标签页和单独的 iframe 调试目标自动应用设置后恢复脚本执行；失败会尝试关闭对应页面并在控制台报告。导航和刷新沿用该页面的设置。
- 不修改 User-Agent、字体或 Emoji。不改变 Windows 时区、输入法、语言、代理、默认浏览器设置或字体；不改变网络 IP。
- 不承诺 Worker / Service Worker / Shared Worker、内部 Chrome 页面及所有网站执行环境的覆盖。此版本不是浏览器内核级抗指纹方案，不保证地区判断或平台风控结果。
- 此环境的网页时钟采用 UTC，可能影响网站的时间显示。登录、Cookie、历史持久保存在 chrome-profile；不是无痕模式。不要在此环境启用 Chrome 同步，以免自行引入日常设置。

## 运行与退出

控制程序需要持续运行才能为新页面应用设置；正常退出会关闭它启动的 Chrome。调试连接断开时会尝试终止自己启动的 Chrome 主进程，避免继续使用设置已失效的窗口。强行结束控制台、系统崩溃等情形可能留下浏览器窗口，此时请手动关闭它们后重启工具。

调试端口使用随机端口并仅绑定本机回环地址，且只连接独立配置。它在运行期间具有控制此独立浏览器的权限，不要将端口转发给其他设备。日常 Chrome 不使用此调试端口。

检测页完全本地运行，不上传检测数据；不控制 Chrome 本身或访问的网站产生的网络流量。字体为有限样本推断。页面不直接验证 HTTP 请求头；附带的冒烟测试会用本地服务器检查请求语言、新页面、刷新与时区。

关闭全部工具浏览器后回到日常 Chrome 即可恢复正常使用。需要清除此工具的登录和历史时，可手动删除 `chrome-profile` 文件夹。原 Firefox 初版留下的 `profile` 文件夹已不使用，不会自动迁移或删除。

## 检查命令

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Launcher.ps1 -CheckOnly`：检查依赖，不启动浏览器。

`node smoke-test.mjs --chrome "C:\Program Files\Google\Chrome\Application\chrome.exe"`：以独立测试目录启动无界面 Chrome，只访问本地文件和本机测试服务器；结果、截图保存在 `.test-artifacts` 中，结束后关闭测试浏览器。

## 官方参考

- 独立数据目录：https://www.chromium.org/developers/creating-and-using-profiles/
- 调试连接与独立目录：https://developer.chrome.com/blog/remote-debugging-port
- 时区和区域接口：https://chromedevtools.github.io/devtools-protocol/tot/Emulation/
- 语言参数：https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/common/chrome_switches.h
