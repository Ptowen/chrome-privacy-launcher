import { spawn } from 'node:child_process';
import { readFile, mkdir, open, unlink } from 'node:fs/promises';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

export const root = dirname(fileURLToPath(import.meta.url));
const autoAttach = { autoAttach: true, waitForDebuggerOnStart: true, flatten: true };

class Protocol {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 0;
    this.pending = new Map();
    this.onEvent = () => {};
    this.closed = new Promise(resolve => socket.addEventListener('close', resolve, { once: true }));
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const pending = this.pending.get(message.id);
        if (!pending) return;
        this.pending.delete(message.id);
        clearTimeout(pending.timer);
        if (message.error) pending.reject(new Error(`${pending.method}: ${message.error.message}`));
        else pending.resolve(message.result);
      } else this.onEvent(message);
    });
    socket.addEventListener('close', () => {
      for (const pending of this.pending.values()) {
        clearTimeout(pending.timer);
        pending.reject(new Error('Chrome 连接已断开'));
      }
      this.pending.clear();
    });
  }
  send(method, params = {}, sessionId) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method}: 超时`));
      }, 10000);
      this.pending.set(id, { resolve, reject, timer, method });
      try { this.socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) })); }
      catch (error) { clearTimeout(timer); this.pending.delete(id); reject(error); }
    });
  }
}

async function socketConnect(url) {
  const socket = new WebSocket(url);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { socket.close(); reject(new Error('连接 Chrome 超时')); }, 8000);
    socket.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
    socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('无法连接 Chrome')); }, { once: true });
  });
  return new Protocol(socket);
}

export async function startSession({ chrome, profile = join(root, 'chrome-profile'), headless = false, onStatus = console.log }) {
  if (typeof WebSocket !== 'function') throw new Error('需要支持内置 WebSocket 的 Node.js 22.4 或更高版本');
  profile = resolve(profile);
  await mkdir(profile, { recursive: true });
  const leasePath = join(profile, '.launcher-lock');
  let lease;
  try { lease = await open(leasePath, 'wx'); }
  catch (error) {
    if (error.code !== 'EEXIST') throw error;
    const pid = Number(await readFile(leasePath, 'utf8'));
    if (!Number.isInteger(pid) || pid <= 0) throw new Error('启动锁异常，请检查工具是否已打开');
    try { process.kill(pid, 0); throw new Error('工具已运行，请使用已打开的窗口'); }
    catch (error) { if (error.code !== 'ESRCH') throw error; }
    await unlink(leasePath);
    lease = await open(leasePath, 'wx');
  }
  await lease.writeFile(String(process.pid));
  let child, protocol, stopping = false, released = false;
  const portFile = join(profile, 'DevToolsActivePort');
  const release = async () => {
    if (released) return;
    released = true;
    await lease.close();
    await unlink(leasePath).catch(() => {});
  };
  const stop = async () => {
    if (stopping) return;
    stopping = true;
    if (protocol) await protocol.send('Browser.close').catch(() => {});
    if (child && child.exitCode === null && child.signalCode === null) {
      await Promise.race([new Promise(resolve => child.once('exit', resolve)), delay(2000)]);
      if (child.exitCode === null && child.signalCode === null) child.kill();
    }
    protocol?.socket.close();
    await release();
  };
  try {
    // Never attach to a browser left running by a previous controller.
    const old = await readFile(portFile, 'utf8').catch(() => '');
    const oldPort = Number(old.split('\n')[0]);
    if (oldPort > 0 && oldPort < 65536) {
      const active = await fetch(`http://127.0.0.1:${oldPort}/json/version`, { signal: AbortSignal.timeout(800) }).then(r => r.ok).catch(() => false);
      if (active) throw new Error('此独立环境仍有 Chrome 在运行，请先关闭其全部窗口');
    }
    await unlink(portFile).catch(error => { if (error.code !== 'ENOENT') throw error; });
    const args = [
      `--user-data-dir=${profile}`, '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0',
      '--lang=en-US', '--accept-lang=en-US,en', '--no-first-run', '--no-default-browser-check',
      '--disable-background-mode', '--disable-session-crashed-bubble',
      ...(headless ? ['--headless=new'] : []), 'about:blank'
    ];
    child = spawn(chrome, args, { stdio: 'ignore', windowsHide: true });
    let launchError;
    child.on('error', error => { launchError = error; });
    let endpoint;
    for (let count = 0; count < 100; count++) {
      if (launchError) throw launchError;
      if (child.exitCode !== null) throw new Error('Chrome 提前退出，可能是此配置已被占用');
      const lines = (await readFile(portFile, 'utf8').catch(() => '')).trim().split('\n');
      if (/^\d+$/.test(lines[0]) && lines[1]?.startsWith('/devtools/browser/')) {
        endpoint = `ws://127.0.0.1:${Number(lines[0])}${lines[1].trim()}`;
        break;
      }
      await delay(150);
    }
    if (!endpoint) throw new Error('Chrome 未提供调试连接，请检查企业策略或配置占用');
    protocol = await socketConnect(endpoint);
    const sessions = new Map();
    const failures = [];
    const targets = new Map();
    const configure = async ({ sessionId, targetInfo }) => {
      try {
        if (targetInfo.type === 'page' || targetInfo.type === 'iframe') {
          await protocol.send('Emulation.setTimezoneOverride', { timezoneId: 'UTC' }, sessionId);
          // --lang supplies the process-wide locale. Per-target locale overrides
          // compete when multiple targets share a renderer process.
          await protocol.send('Target.setAutoAttach', autoAttach, sessionId);
          sessions.set(targetInfo.targetId, sessionId);
        }
        // Workers are resumed without a promise of timezone coverage.
        await protocol.send('Runtime.runIfWaitingForDebugger', {}, sessionId);
        targets.set(targetInfo.targetId, { type: targetInfo.type, sessionId });
      } catch (error) {
        failures.push(String(error.message));
        onStatus(`配置失败，关闭对应页面：${error.message}`);
        await protocol.send('Target.closeTarget', { targetId: targetInfo.targetId }).catch(() => {});
      }
    };
    protocol.onEvent = message => {
      if (message.method === 'Target.attachedToTarget') void configure(message.params);
      if (message.method === 'Target.detachedFromTarget') {
        for (const [id, session] of sessions) if (session === message.params.sessionId) sessions.delete(id);
      }
    };
    await protocol.send('Target.setAutoAttach', autoAttach);
    const waitForPage = async targetId => {
      for (let count = 0; count < 100; count++) {
        if (sessions.has(targetId) && targets.has(targetId)) return sessions.get(targetId);
        if (failures.length) throw new Error(failures.at(-1));
        await delay(100);
      }
      throw new Error('页面配置超时');
    };
    const createPage = async url => {
      const { targetId } = await protocol.send('Target.createTarget', { url: 'about:blank' });
      const sessionId = await waitForPage(targetId);
      await protocol.send('Page.navigate', { url }, sessionId);
      return { targetId, sessionId };
    };
    const existing = await protocol.send('Target.getTargets');
    for (const target of existing.targetInfos.filter(t => t.type === 'page')) {
      const sessionId = await waitForPage(target.targetId);
      await protocol.send('Page.navigate', { url: pathToFileURL(join(root, 'check.html')).href }, sessionId);
    }
    if (!existing.targetInfos.some(t => t.type === 'page')) await createPage(pathToFileURL(join(root, 'check.html')).href);
    const closed = protocol.closed.then(async () => {
      if (!stopping && child.exitCode === null && child.signalCode === null) child.kill();
      await release();
    });
    return { protocol, createPage, waitForPage, sessions, targets, failures, stop, closed };
  } catch (error) { await stop(); throw error; }
}

async function main() {
  const index = process.argv.indexOf('--chrome');
  const chrome = index >= 0 ? process.argv[index + 1] : process.env.CHROME_BINARY;
  if (!chrome) throw new Error('未找到 Chrome，请通过 Start.cmd 启动');
  console.log('Chrome 独立隐私环境\n只调整此环境的语言、网页时区和区域格式。字体 / Emoji / IP 保持实际值。');
  console.log('请保留此窗口。退出请按 Ctrl+C；将一并关闭此工具启动的 Chrome。');
  const profileIndex = process.argv.indexOf('--profile');
  const session = await startSession({ chrome, ...(profileIndex >= 0 ? { profile: process.argv[profileIndex + 1] } : {}) });
  const shutdown = () => { void session.stop(); };
  process.once('SIGINT', shutdown);
  process.once('SIGTERM', shutdown);
  if (process.argv.includes('--desktop')) {
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', data => { if (data.includes('stop')) shutdown(); });
    process.stdin.once('end', shutdown);
    process.stdin.resume();
  }
  console.log('已启动。请在检测页核对 UTC、英语和区域格式。');
  await session.closed;
  await session.stop();
  if (process.argv.includes('--desktop')) process.stdin.destroy();
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(`启动失败：${error.message}`); process.exitCode = 1; });
}
