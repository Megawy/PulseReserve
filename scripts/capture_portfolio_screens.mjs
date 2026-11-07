import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const chromePath = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT_DIR = 'F:\\Course\\github\\PulseReserve\\Mostaql_Portfolio_PulseReserve';

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

let idSeq = 1;
function sendCdp(ws, method, params = {}) {
  const id = idSeq++;
  return new Promise((resolve, reject) => {
    const handler = (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id === id) {
        ws.removeEventListener('message', handler);
        if (msg.error) reject(new Error(msg.error.message));
        else resolve(msg.result);
      }
    };
    ws.addEventListener('message', handler);
    ws.send(JSON.stringify({ id, method, params }));
  });
}

console.log('1. Starting showcase server...');
const serverProc = spawn('node', ['scripts/showcase_server.mjs'], { stdio: 'inherit' });

console.log('2. Starting Chrome headless...');
const chrome = spawn(chromePath, [
  '--headless=new',
  '--remote-debugging-port=9222',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  '--window-size=1920,1080',
]);

try {
  await sleep(2500);
  const targetsRes = await fetch('http://localhost:9222/json/list');
  const targets = await targetsRes.json();
  const pageTarget = targets.find((t) => t.type === 'page') || targets[0];
  console.log('Connected to target:', pageTarget.webSocketDebuggerUrl);

  const ws = new WebSocket(pageTarget.webSocketDebuggerUrl);
  await new Promise((resolve) => ws.addEventListener('open', resolve));

  await sendCdp(ws, 'Emulation.setDeviceMetricsOverride', {
    width: 1920,
    height: 1080,
    deviceScaleFactor: 1.25,
    mobile: false,
  });

  await sendCdp(ws, 'Page.enable');

  const capture = async (url, filename, waitMs = 2500, clickSelectors = []) => {
    console.log(`Navigating to ${url}...`);
    await sendCdp(ws, 'Page.navigate', { url });
    await sleep(waitMs);

    for (const sel of clickSelectors) {
      console.log(`Clicking selector: ${sel}`);
      await sendCdp(ws, 'Runtime.evaluate', {
        expression: `
          (() => {
            const el = document.querySelector('${sel}');
            if (el) el.click();
          })()
        `,
      });
      await sleep(1000);
    }

    console.log(`Capturing screenshot -> ${filename}...`);
    const screenshot = await sendCdp(ws, 'Page.captureScreenshot', { format: 'png' });
    fs.writeFileSync(path.join(OUT_DIR, filename), Buffer.from(screenshot.data, 'base64'));
    console.log(`Saved: ${filename}`);
  };

  // Screen 1: Event Catalog
  await capture('http://localhost:4255/', '01_Events_Catalog_Explorer.png', 2500);

  // Screen 2: Interactive Seat Map
  await capture('http://localhost:4255/events/11111111-1111-1111-1111-111111111111', '02_Interactive_Seat_Map_Live_Booking.png', 3000, [
    '.seat.available',
  ]);

  // Screen 3: Admin Operations Dashboard
  await capture('http://localhost:4255/admin/payments', '03_Admin_Financial_Ops_Dashboard.png', 2500);

  // Screen 4: Admin Reconciliation Tasks
  await capture('http://localhost:4255/admin/payments/reconciliation', '04_Admin_Reconciliation_Tasks.png', 2500);

  // Screen 5: Customer My Orders & Issued Tickets
  await capture('http://localhost:4255/account/orders', '05_Customer_Orders_and_Tickets.png', 2500);

  ws.close();
} catch (err) {
  console.error('Error during capture:', err);
} finally {
  console.log('Shutting down Chrome and server...');
  chrome.kill();
  serverProc.kill();
  process.exit(0);
}
