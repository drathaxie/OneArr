// First-run sign-in and dashboard check in a real Chromium window (headless). Reads the password from the environment.
import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const out = process.argv[2];
const password = process.env.TRAY_TEST_PASSWORD;
if (!password) throw new Error('TRAY_TEST_PASSWORD is not set');

const results = { firstRunShown: false, passwordAccepted: false, dashboardOpened: false, errors: [] };
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  page.on('pageerror', (e) => results.errors.push(String(e)));
  await page.goto('http://127.0.0.1:8780/', { waitUntil: 'networkidle', timeout: 60000 });
  results.firstRunShown = (await page.textContent('body')).includes('First run');
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await page.waitForSelector('nav.sidebar', { timeout: 30000 });
  results.passwordAccepted = true;
  results.dashboardOpened = await page.isVisible('button.nav:has-text("Dashboard")');
  await page.screenshot({ path: `${out}.png`, fullPage: true });
} catch (e) {
  results.errors.push(String(e));
} finally {
  await browser.close();
  writeFileSync(`${out}.json`, JSON.stringify(results, null, 2));
}
const ok = results.firstRunShown && results.passwordAccepted && results.dashboardOpened && results.errors.length === 0;
console.log(`UI: firstRun=${results.firstRunShown} passwordAccepted=${results.passwordAccepted} dashboard=${results.dashboardOpened} errors=${results.errors.length}`);
process.exit(ok ? 0 : 1);
