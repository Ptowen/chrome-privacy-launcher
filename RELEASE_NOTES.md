# v1.0.0

Windows 10/11 x64 的 Chrome 独立环境启动器。

下载 `ChromePrivacy.exe`，双击后点击「启动独立 Chrome」。已内置运行环境，电脑只需预先安装 Google Chrome，无需管理员权限。

- 独立浏览器数据目录，英语语言与默认区域格式，网页时区为 UTC。
- 提供启动、停止按钮与本地环境检测页。
- 不改变 Windows 时区、系统语言、输入法或日常 Chrome 配置。
- 已通过打包后自检，验证新标签页、弹窗、刷新、关闭其他标签页后继续导航以及请求语言。

此版本不配置代理、不改变 IP，也未限制 WebRTC 直连。字体和 Emoji 保持实际值，不保证平台的地区判断。Worker 和 Chrome 内部页面等环境不承诺覆盖。

运行期间保留启动器窗口，关闭窗口会关闭其启动的 Chrome。登录和历史保存在 `%LOCALAPPDATA%\ChromePrivacyLauncher\chrome-profile`。

附件 `SHA256.txt` 可用于核对下载文件。程序未进行代码签名。
