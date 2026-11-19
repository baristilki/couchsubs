// SPDX-License-Identifier: AGPL-3.0-only
// Samsung TV (tv-tizen/platform.js): ultrawide ekranda sayfa görünümü ve HDMI penceresi (#304). Sayfa sahte Tizen
// API'leriyle Node'da çalıştırılır; TV ya da tarayıcı gerekmez. Çalıştırma: node --test scripts/tv-tizen.test.js
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const tizenDir = path.join(__dirname, '..', 'tv-tizen');
const code = fs.readFileSync(path.join(tizenDir, 'platform.js'), 'utf8');

// innerWidth/innerHeight: uygulama yüzeyinin 1920 piksellik görünümdeki ölçüsü.
function load(innerWidth, innerHeight) {
  const meta = { content: 'width=1920', setAttribute(name, value) { if (name === 'content') this.content = value; } };
  const shown = [];
  const window = { innerWidth, innerHeight, addEventListener() {} };
  const tizen = {
    systeminfo: {
      getPropertyValue(name, ok) { ok({ connected: [{ type: 'HDMI', number: 1 }], disconnected: [] }); },
    },
    tvwindow: {
      setSource(source, ok) { ok(); },
      show(ok, fail, rect, type, z) { shown.push({ rect, type, z }); ok(); },
    },
  };
  const document = { querySelector: (q) => (q === 'meta[name="viewport"]' ? meta : null) };
  vm.runInNewContext(code, { window, document, tizen });
  return { platform: window.COUCHSUBS_PLATFORM, meta, shown };
}

test('16:9 TV (1080p ve 4K): görünüm 1920 piksel kalır', () => {
  assert.equal(load(1920, 1080).meta.content, 'width=1920');
  // Yuvarlama payı: 16:9'a yakın yüzey de değişmez.
  assert.equal(load(1920, 1079).meta.content, 'width=1920');
  // 16:10 ve 4:3 de geniş sayılmaz.
  assert.equal(load(1920, 1200).meta.content, 'width=1920');
});

test('21:9 ultrawide yüzey (Odyssey G8 3440x1440): sayfa 1080 piksel yükseklikte, ekran genişliğinde', () => {
  // 3440x1440 yüzey, 1920 piksellik görünümde 1920x804 ölçülür.
  assert.equal(load(1920, 804).meta.content, 'width=2579');
  // 2560x1080 yüzey.
  assert.equal(load(1920, 810).meta.content, 'width=2560');
});

test('32:9 yüzey de geniş sayılır', () => {
  assert.equal(load(1920, 540).meta.content, 'width=3840');
});

test('viewportFor: geçersiz ölçüde dokunmaz, oranı korur', () => {
  const { platform } = load(1920, 1080);
  assert.equal(platform.viewportFor(0, 0), null);
  assert.equal(platform.viewportFor(undefined, 1080), null);
  assert.equal(platform.viewportFor(3440, 1440), 'width=2580');
  assert.equal(platform.viewportFor(5120, 1440), 'width=3840');
  assert.equal(platform.viewportFor(3840, 2160), null);
  assert.equal(platform.viewportFor(1440, 1080), null);
});

test('HDMI penceresi yüzeyin tamamını kaplar, sayfanın arkasında açılır', () => {
  for (const [w, h] of [[1920, 1080], [1920, 804]]) {
    const { platform, shown } = load(w, h);
    const statuses = [];
    platform.showHdmi(1, null, (s) => statuses.push(s));
    assert.deepEqual(statuses, ['loading', 'playing']);
    assert.equal(shown.length, 1);
    assert.deepEqual(JSON.parse(JSON.stringify(shown[0])), { rect: ['0', '0', '100%', '100%'], type: 'MAIN', z: 'BEHIND' });
  }
});

test('config.xml: yüzey ekranın tamamı (base_screen_resolution=fullscreen)', () => {
  const config = fs.readFileSync(path.join(tizenDir, 'config.xml'), 'utf8');
  assert.match(config, /<tizen:metadata key="http:\/\/tizen\.org\/metadata\/app_ui_type\/base_screen_resolution" value="fullscreen"\/>/);
});
