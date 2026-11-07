using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Chrome 独立环境启动器")]
[assembly: AssemblyDescription("独立 Chrome 环境：英语、UTC 时区、本地检测")]
[assembly: AssemblyVersion("1.1.0.0")]

static class Program
{
    public static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromePrivacyLauncher");
    public static string Quote(string value) { return "\"" + value.Replace("\"", "") + "\""; }
    public static string FindChrome()
    {
        string[] paths = {
            Environment.GetEnvironmentVariable("CHROME_BINARY"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };
        foreach (string path in paths) if (!String.IsNullOrEmpty(path) && File.Exists(path)) return path;
        return "";
    }
    static Stream Resource(string name)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload." + name);
        if (stream == null) throw new IOException("缺少内置资源：" + name);
        return stream;
    }
    static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    public static string Extract(string destination)
    {
        string cache = Path.Combine(destination, "runtime", Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N"));
        Directory.CreateDirectory(cache);
        string manifest;
        using (var reader = new StreamReader(Resource("hashes.txt"), Encoding.UTF8)) manifest = reader.ReadToEnd();
        foreach (string line in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] entry = line.Split(new[] { ' ' }, 2);
            string name = entry[1].Trim();
            if (Path.GetFileName(name) != name) throw new IOException("资源名称无效");
            string target = Path.Combine(cache, name);
            if (File.Exists(target) && Hash(target) == entry[0]) continue;
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (Stream source = Resource(name == "node.exe" ? "node.exe.gz" : name))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    if (name == "node.exe") using (var gzip = new GZipStream(source, CompressionMode.Decompress)) gzip.CopyTo(output);
                    else source.CopyTo(output);
                }
                if (Hash(temporary) != entry[0]) throw new IOException("内置文件校验失败：" + name);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return cache;
    }
    public static ProcessStartInfo NodeStart(string runtime, string arguments)
    {
        return new ProcessStartInfo(Path.Combine(runtime, "node.exe"), arguments) {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test")
        {
            string testRoot = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(testRoot);
            try
            {
                string runtime = Extract(testRoot);
                string chrome = FindChrome();
                if (String.IsNullOrEmpty(chrome)) throw new IOException("未发现 Chrome");
                using (var job = new ChildJob())
                using (var child = new Process())
                {
                    child.StartInfo = NodeStart(runtime, Quote(Path.Combine(runtime, "smoke-test.mjs")) + " --chrome " + Quote(chrome));
                    child.Start();
                    job.Assign(child);
                    Task<string> output = child.StandardOutput.ReadToEndAsync();
                    Task<string> error = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(90000)) throw new IOException("测试超时");
                    Task.WaitAll(output, error);
                    File.WriteAllText(Path.Combine(testRoot, "self-test.log"), output.Result + "\r\n" + error.Result, Encoding.UTF8);
                    return child.ExitCode;
                }
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(testRoot, "self-test-error.txt"), error.ToString()); return 1; }
        }
        bool created;
        using (var singleton = new Mutex(true, @"Local\ChromePrivacyLauncher-" + Environment.UserName, out created))
        {
            if (!created) { MessageBox.Show("启动器已经打开，请使用已有窗口。", "Chrome 独立环境"); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
            return 0;
        }
    }
}

// Closing this application's job handle also closes its own runtime and browser.
// Existing Chrome processes are never assigned to this job.
sealed class ChildJob : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    IntPtr handle;
    public ChildJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new IOException("无法创建独立进程组");
        var limits = new ExtendedLimits();
        limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf(limits);
        IntPtr data = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(limits, data, false);
            if (!SetInformationJobObject(handle, 9, data, (uint)size)) { Dispose(); throw new IOException("无法设置独立进程组"); }
        } finally { Marshal.FreeHGlobal(data); }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) {
            try { process.Kill(); } catch { }
            throw new IOException("无法隔离启动进程，请关闭后重试");
        }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}

sealed class LauncherForm : Form
{
    TextBox chromePath = new TextBox(), log = new TextBox();
    Button start = new Button(), stop = new Button(), browse = new Button();
    RadioButton persistent = new RadioButton(), temporary = new RadioButton();
    Button retry = new Button();
    string lastTemporary;
    Process controller;
    ChildJob job;
    bool stopping, closing, busy;
    string FindPendingTemporary()
    {
        string temporaryRoot = Path.Combine(Program.DataRoot, "temporary-profiles");
        if (!Directory.Exists(temporaryRoot)) return null;
        foreach (string path in Directory.GetDirectories(temporaryRoot, "session-*", SearchOption.TopDirectoryOnly))
            if (File.Exists(Path.Combine(path, ".temporary-session.json"))) return path;
        return null;
    }
    public LauncherForm()
    {
        Text = "Chrome 独立环境启动器";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(680, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Icon = SystemIcons.Application;
        var heading = new Label { Text = "Chrome 独立环境", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Location = new Point(24, 22) };
        var description = new Label { Text = "英语 · UTC 时区 · 独立登录与历史\r\n电脑设置和日常 Chrome 配置保持原样。", Location = new Point(24, 78), Size = new Size(625, 58) };
        chromePath.SetBounds(24, 146, 500, 30);
        chromePath.Text = Program.FindChrome();
        browse.Text = "选择 Chrome";
        browse.SetBounds(535, 144, 120, 32);
        browse.Click += delegate {
            using (var picker = new OpenFileDialog { Filter = "Google Chrome|chrome.exe", Title = "选择 Chrome" })
                if (picker.ShowDialog() == DialogResult.OK) chromePath.Text = picker.FileName;
        };
        start.Text = "启动独立 Chrome";
        persistent.Text = "保留登录（默认）";
        persistent.SetBounds(24, 188, 250, 28);
        persistent.Checked = true;
        temporary.Text = "临时浏览";
        temporary.SetBounds(340, 188, 250, 28);
        var modeHint = new Label { Text = "保留登录：下次继续使用；临时浏览：每次新环境，退出后清理。", Location = new Point(24, 224), Size = new Size(631, 30) };
        start.SetBounds(24, 265, 200, 46);
        start.BackColor = Color.FromArgb(36, 90, 67);
        start.ForeColor = Color.White;
        stop.Text = "停止并关闭此环境";
        stop.SetBounds(236, 265, 205, 46);
        stop.Enabled = false;
        start.Click += async delegate { await StartBrowser(); };
        stop.Click += async delegate { await StopBrowser(); };
        retry.Text = "重试清理临时数据";
        retry.SetBounds(453, 265, 202, 46);
        retry.Enabled = false;
        retry.Click += async delegate {
            if (busy || String.IsNullOrEmpty(lastTemporary)) return;
            busy = true;
            start.Enabled = retry.Enabled = false;
            try {
                string runtime = await Task.Run(() => Program.Extract(Program.DataRoot));
                using (var cleanup = new Process()) {
                    cleanup.StartInfo = Program.NodeStart(runtime, Program.Quote(Path.Combine(runtime, "chrome-launcher.mjs")) + " --cleanup-temp " + Program.Quote(lastTemporary) + " --temporary-root " + Program.Quote(Path.Combine(Program.DataRoot, "temporary-profiles")));
                    cleanup.Start();
                    Task<string> output = cleanup.StandardOutput.ReadToEndAsync();
                    Task<string> error = cleanup.StandardError.ReadToEndAsync();
                    await Task.Run(() => cleanup.WaitForExit());
                    Log(await output); Log(await error);
                    if (cleanup.ExitCode == 0) lastTemporary = FindPendingTemporary();
                }
            } catch (Exception error) { Log("清理失败：" + error.Message); }
            finally { busy = false; start.Enabled = true; retry.Enabled = !String.IsNullOrEmpty(lastTemporary); }
        };
        log.SetBounds(24, 330, 631, 160);
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        var note = new Label { Text = "此版本不改变 IP，也未限制 WebRTC 直连；字体和 Emoji 保持实际值。\r\n使用期间保留此窗口，关闭窗口会关闭本工具启动的 Chrome。\r\n数据位置：%LOCALAPPDATA%\\ChromePrivacyLauncher", Location = new Point(24, 510), Size = new Size(631, 70), ForeColor = Color.DimGray };
        Controls.AddRange(new Control[] { heading, description, chromePath, browse, persistent, temporary, modeHint, start, stop, retry, log, note });
        FormClosing += OnClosing;
        Log("免安装，已内置运行环境。点击上方按钮开始。");
        lastTemporary = FindPendingTemporary();
        retry.Enabled = lastTemporary != null;
        if (retry.Enabled) Log("发现上次未清理的临时数据，可点击重试清理：" + lastTemporary);
    }
    void Log(string message)
    {
        if (String.IsNullOrEmpty(message) || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => Log(message))); } catch (InvalidOperationException) { } return; }
        log.AppendText(message + Environment.NewLine);
    }
    async Task StartBrowser()
    {
        if (busy || controller != null) return;
        string chrome = chromePath.Text.Trim().Trim('"');
        if (!File.Exists(chrome) || !String.Equals(Path.GetFileName(chrome), "chrome.exe", StringComparison.OrdinalIgnoreCase)) {
            MessageBox.Show(this, "请选择已安装的 Google Chrome 的 chrome.exe。", Text); return;
        }
        busy = true;
        start.Enabled = browse.Enabled = chromePath.Enabled = persistent.Enabled = temporary.Enabled = retry.Enabled = false;
        try
        {
            Log("正在准备内置运行环境，首次启动需要几秒钟……");
            string runtime = await Task.Run(() => Program.Extract(Program.DataRoot));
            job = new ChildJob();
            controller = new Process();
            controller.StartInfo = Program.NodeStart(runtime, Program.Quote(Path.Combine(runtime, "chrome-launcher.mjs")) + " --desktop --chrome " + Program.Quote(chrome) + " --profile " + Program.Quote(Path.Combine(Program.DataRoot, "chrome-profile")) + " --mode " + (temporary.Checked ? "temporary" : "persistent") + " --temporary-root " + Program.Quote(Path.Combine(Program.DataRoot, "temporary-profiles")));
            controller.OutputDataReceived += (sender, e) => {
                if (e.Data == null) return;
                if (e.Data.StartsWith("TEMP_PROFILE=")) { BeginInvoke((Action)(() => lastTemporary = e.Data.Substring(13))); return; }
                if (!e.Data.StartsWith("请保留此窗口")) Log(e.Data);
            };
            controller.ErrorDataReceived += (sender, e) => Log(e.Data);
            controller.Start();
            job.Assign(controller);
            controller.BeginOutputReadLine();
            controller.BeginErrorReadLine();
            stop.Enabled = true;
            var current = controller;
            await Task.Run(() => current.WaitForExit());
            Log("独立环境已停止。");
        }
        catch (Exception error) { Log("启动失败：" + error.Message); }
        finally
        {
            stop.Enabled = false;
            if (job != null) { job.Dispose(); job = null; }
            if (controller != null) { controller.Dispose(); controller = null; }
            busy = false;
            start.Enabled = browse.Enabled = chromePath.Enabled = persistent.Enabled = temporary.Enabled = true;
            lastTemporary = FindPendingTemporary();
            retry.Enabled = lastTemporary != null;
        }
    }
    async Task StopBrowser()
    {
        if (stopping) return;
        stopping = true;
        stop.Enabled = false;
        var current = controller;
        try
        {
            if (current != null && !current.HasExited) {
                Log("正在关闭此独立环境……");
                current.StandardInput.WriteLine("stop");
                current.StandardInput.Flush();
                await Task.Run(() => { try { current.WaitForExit(20000); } catch (InvalidOperationException) { } });
            }
        }
        catch (Exception error) { Log("关闭提示：" + error.Message); }
        finally { if (job != null) job.Dispose(); stopping = false; }
    }
    async void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (closing) return;
        if (busy && controller == null) { e.Cancel = true; Log("正在准备文件，请稍后关闭。"); return; }
        if (controller == null) return;
        e.Cancel = true;
        await StopBrowser();
        if (!String.IsNullOrEmpty(lastTemporary) && Directory.Exists(lastTemporary)) MessageBox.Show(this, "临时数据尚未清理，文件可能仍被占用。关闭对应浏览器后，请清理此目录：\r\n" + lastTemporary, "临时数据仍保留");
        closing = true;
        Close();
    }
}
