import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { root, startSession } from './chrome-launcher.mjs';

const chrome = process.argv[process.argv.indexOf('--chrome') + 1];
if (!chrome || !process.argv.includes('--chrome')) throw new Error('Pass --chrome <chrome.exe>');
const artifacts = join(root, '.test-artifacts', String(Date.now()));
await mkdir(artifacts, { recursive: true });
let serial = 0;
const headers = [];
const server = createServer((req, res) => {
  headers.push(req.headers['accept-language']);
  res.setHeader('Content-Type', 'text/html; charset=utf-8');
  res.setHeader('Cache-Control', 'no-store');
  res.end(`<!doctype html><title>Local fixture</title><script>
window.snapshot={serial:${++serial},timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,locale:Intl.DateTimeFormat().resolvedOptions().locale,languages:Array.from(navigator.languages),offset:new Date().getTimezoneOffset()};
<\/script><p>Local browser environment fixture</p>`);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
let session;
const report = {};
try {
  session = await startSession({ chrome, profile: join(artifacts, 'profile'), headless: true });
  const evaluate = async (sessionId, expression) => {
    const result = await session.protocol.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true }, sessionId);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  const waitSnapshot = async (id, after = 0) => {
    for (let i = 0; i < 100; i++) {
      const result = await evaluate(id, 'window.snapshot || null');
      if (result && result.serial > after) {
        assert.equal(result.timeZone, 'UTC');
        assert.equal(result.offset, 0);
        assert.equal(result.locale, 'en-US');
        assert.equal(result.languages[0], 'en-US');
        return result;
      }
      await delay(100);
    }
    throw new Error('Fixture did not load');
  };
  // Run the shipped diagnostic and check it actually rendered its values.
  const first = [...session.sessions.values()][0];
  let diagnostic;
  for (let i = 0; i < 60; i++) {
    diagnostic = await evaluate(first, 'document.getElementById("result")?.textContent || ""');
    if (diagnostic) break;
    await delay(100);
  }
  assert.match(diagnostic, /已观察到/);
  report.diagnostic = diagnostic;
  const metrics = await session.protocol.send('Page.getLayoutMetrics', {}, first);
  const size = metrics.cssContentSize;
  const screenshot = await session.protocol.send('Page.captureScreenshot', {
    format: 'png', captureBeyondViewport: true,
    clip: { x: 0, y: 0, width: size.width, height: size.height, scale: 1 }
  }, first);
  await writeFile(join(artifacts, 'diagnostic.png'), Buffer.from(screenshot.data, 'base64'));
  const page = await session.createPage(url);
  report.initialScript = await waitSnapshot(page.sessionId);
  await session.protocol.send('Page.reload', {}, page.sessionId);
  report.reload = await waitSnapshot(page.sessionId, report.initialScript.serial);
  const second = await session.createPage(url + 'second');
  report.newTab = await waitSnapshot(second.sessionId);
  const popupId = await evaluate(page.sessionId, `window.open(${JSON.stringify(url + 'popup')}, '_blank'); true`);
  assert.equal(popupId, true);
  let popup;
  for (let i = 0; i < 80; i++) {
    const { targetInfos } = await session.protocol.send('Target.getTargets');
    popup = targetInfos.find(t => t.url === url + 'popup');
    if (popup) break;
    await delay(100);
  }
  assert.ok(popup, 'Popup must be found');
  report.popup = await waitSnapshot(await session.waitForPage(popup.targetId));
  // Closing an opener or another tab must not remove the remaining tab's override.
  await session.protocol.send('Target.closeTarget', { targetId: popup.targetId });
  await session.protocol.send('Target.closeTarget', { targetId: page.targetId });
  await session.protocol.send('Page.navigate', { url: url + 'after-close' }, second.sessionId);
  report.afterClosingOtherTabs = await waitSnapshot(second.sessionId, report.newTab.serial);
  assert.ok(headers.length >= 4);
  assert.ok(headers.every(value => /^en-US(?:,|$)/.test(value)), JSON.stringify(headers));
  report.acceptLanguage = [...new Set(headers)];
  assert.deepEqual(session.failures, []);
  await assert.rejects(startSession({ chrome, profile: join(artifacts, 'profile'), headless: true }), /工具已运行/);
  report.duplicateLaunchBlocked = true;
  const active = await session.protocol.send('Browser.getVersion');
  report.chromeVersion = active.product;
  report.status = 'passed';
  await writeFile(join(artifacts, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ...report, artifacts }, null, 2));
} finally {
  if (session) await session.stop();
  await new Promise(resolve => server.close(resolve));
}
