// Minimal CDP helper for automated R1 checks.
// usage: node tools/cdp.mjs [port] [expression]
const port = process.argv[2] || '9333';
const expr = process.argv[3] || 'document.title';
const verbose = process.argv.includes('--verbose');

const list = await fetch(`http://127.0.0.1:${port}/json/list`).then(r => r.json());
const pages = list.filter(t => t.type === 'page');
if (!pages.length) { console.log('NO PAGE TARGETS', JSON.stringify(list.map(t => t.type))); process.exit(2); }
if (verbose) console.log(pages.map(p => `${p.title} :: ${p.url}`).join('\n'));

const target = pages.find(p => p.url.includes('r1.internal') || true) || pages[0];
const ws = new WebSocket(target.webSocketDebuggerUrl);
let id = 0;
const pending = new Map();

function send(method, params = {}, ms = 6000) {
  return new Promise((res, rej) => {
    const msgId = ++id;
    pending.set(msgId, { res, rej });
    ws.send(JSON.stringify({ id: msgId, method, params }));
    const t = setTimeout(() => {
      if (!pending.has(msgId)) return;
      pending.delete(msgId);
      rej(new Error(`timeout waiting for ${method}`));
    }, ms);
    t.unref?.();
  });
}

ws.addEventListener('message', ev => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const { res } = pending.get(msg.id);
    pending.delete(msg.id);
    res(msg);
  } else if (msg.method === 'Runtime.exceptionThrown' && verbose) {
    console.log('EXCEPTION', JSON.stringify(msg.params.exceptionDetails).slice(0, 600));
  } else if (msg.method === 'Runtime.consoleAPICalled' && verbose) {
    console.log('CONSOLE', msg.params.type, msg.params.args.map(a => a.value).join(' '));
  }
});

ws.addEventListener('error', e => { console.log('WS ERROR', e.message || e); process.exit(3); });

await new Promise((res, rej) => { ws.addEventListener('open', res); setTimeout(() => rej(new Error('open timeout')), 6000); });
await send('Runtime.enable');
let r;
try {
  r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
} catch (e) {
  console.log('NO RESPONSE (' + e.message + ') - renderer likely crashed');
  ws.close();
  process.exit(4);
}
if (r.error) console.log('CDP ERROR', JSON.stringify(r.error));
else if (r.result?.exceptionDetails) console.log('EVAL EXCEPTION', JSON.stringify(r.result.exceptionDetails).slice(0, 800));
else console.log(typeof r.result.result.value === 'string' ? r.result.result.value : JSON.stringify(r.result.result.value));
ws.close();
process.exit(0);
