/* HALOFLUX desktop - canvas engine.
   Wide Saturn-style annulus + meteor field. Every streak and ring band is driven
   by the live telemetry feed, no mock values. */

window.addEventListener('error', function (ev) { window.__hfErr = (ev.message || '?') + ' @' + (ev.lineno || 0); });

const HFX = (() => {
  let canvas, ctx;
  let W = 0, H = 0, DPR = 1;
  let t = 0;
  let rot = 0;
  let running = true;
  let rafId = 0;
  let lastFrame = 0;

  const tg = { cpu: 0, gpu: 0, nd: 0, nu: 0, rd: 0, wr: 0 };
  const sm = { cpu: 0, gpu: 0, nd: 0, nu: 0, rd: 0, wr: 0 };
  let cores = [];
  let coreTarget = [];

  let meteors = [];
  const aud = { level: 0, bands: new Array(32).fill(0) };

  // data arcs: eased value, breathing phase, change pulse
  const arcMeters = [];
  let rotBoost = 0;

  // music progress, interpolated between host updates so it never stutters
  const music = { pos: 0, dur: 0, playing: false, stamp: 0 };
  const audS = { level: 0, bands: new Array(32).fill(0) };
  let sparks = [];
  let specks = [];
  let iconRing = [];
  let winCards = [];

  const sprites = { glow: null, streak: null, grain: null, dust: null, vignette: null, bg: null, body: null };

  /* ---------------- data classes ---------------- */

  const CLS = [
    { k: 'nd', rgb: [118, 216, 255] },
    { k: 'nu', rgb: [104, 255, 208] },
    { k: 'rd', rgb: [255, 198, 122] },
    { k: 'wr', rgb: [255, 138, 178] },
    { k: 'gpu', rgb: [192, 152, 255] },
  ];

  function act(c) {
    const v = sm[c.k] || 0;
    if (c.k === 'gpu') return Math.max(0, Math.min(1, v / 100));
    if (v <= 0) return 0;
    const ref = c.k === 'nd' ? 2.2e6 : c.k === 'nu' ? 0.7e6 : 6e6;
    return Math.max(0, Math.min(1, Math.log10(1 + v / 1024) / Math.log10(1 + ref / 1024)));
  }

  function pickClass() {
    let total = 0;
    for (const c of CLS) total += act(c) + 0.14;
    let r = Math.random() * total;
    for (const c of CLS) {
      r -= act(c) + 0.14;
      if (r <= 0) return c;
    }
    return CLS[0];
  }

  /* ---------------- sprites ---------------- */

  function radial(size, stops) {
    const c = document.createElement('canvas');
    c.width = c.height = size;
    const g = c.getContext('2d');
    const grd = g.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    for (const [p, col] of stops) grd.addColorStop(p, col);
    g.fillStyle = grd;
    g.fillRect(0, 0, size, size);
    return c;
  }

  function makeSprites() {
    sprites.glow = radial(256, [
      [0, 'rgba(255,255,255,1)'],
      [0.08, 'rgba(236,248,255,0.86)'],
      [0.22, 'rgba(190,226,255,0.34)'],
      [0.46, 'rgba(140,190,255,0.09)'],
      [1, 'rgba(80,140,220,0)'],
    ]);

    const s = document.createElement('canvas');
    s.width = 512; s.height = 64;
    const sg = s.getContext('2d');
    const lg = sg.createLinearGradient(0, 0, 512, 0);
    lg.addColorStop(0, 'rgba(140,200,255,0)');
    lg.addColorStop(0.36, 'rgba(190,225,255,0.35)');
    lg.addColorStop(0.5, 'rgba(255,255,255,1)');
    lg.addColorStop(0.64, 'rgba(190,225,255,0.35)');
    lg.addColorStop(1, 'rgba(140,200,255,0)');
    const vg = sg.createLinearGradient(0, 0, 0, 64);
    vg.addColorStop(0, 'rgba(0,0,0,0)');
    vg.addColorStop(0.5, 'rgba(255,255,255,1)');
    vg.addColorStop(1, 'rgba(0,0,0,0)');
    sg.fillStyle = lg;
    sg.fillRect(0, 0, 512, 64);
    sg.globalCompositeOperation = 'destination-in';
    sg.fillStyle = vg;
    sg.fillRect(0, 0, 512, 64);
    sprites.streak = s;

    const g2 = document.createElement('canvas');
    g2.width = g2.height = 128;
    const gg = g2.getContext('2d');
    const img = gg.createImageData(128, 128);
    for (let i = 0; i < img.data.length; i += 4) {
      const v = 120 + Math.random() * 135;
      img.data[i] = img.data[i + 1] = img.data[i + 2] = v;
      img.data[i + 3] = Math.random() * 24;
    }
    gg.putImageData(img, 0, 0);
    sprites.grain = g2;
  }

  /* ---------------- geometry ---------------- */

  // three quarters of a ring: the gap faces down-left

  const ARC = { a0: 0, a1: Math.PI * 2 };

  function geo() {
    const ro = H * 0.52;
    return { cx: W * 0.635, cy: H * 0.445, ro, ri: ro * 0.60 };
  }

  function makeDust() {
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const g = c.getContext('2d');
    g.lineCap = 'round';
    for (let i = 0; i < 220; i++) {
      const x = Math.random() * W;
      const y = Math.random() * H;
      const len = 6 + Math.random() * Math.random() * 220;
      const a = (Math.random() - 0.5) * 0.65;
      const alpha = Math.random() * Math.random() * 0.14;
      g.strokeStyle = `rgba(214,232,248,${alpha})`;
      g.lineWidth = Math.random() < 0.85 ? 0.6 : 1.1;
      g.beginPath();
      g.moveTo(x, y);
      g.lineTo(x + Math.cos(a) * len, y + Math.sin(a) * len);
      g.stroke();
    }
    sprites.dust = c;
  }

  function makeVignette() {
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const g = c.getContext('2d');
    const grd = g.createRadialGradient(W * 0.55, H * 0.45, Math.min(W, H) * 0.12,
      W * 0.55, H * 0.45, Math.max(W, H) * 0.78);
    grd.addColorStop(0, 'rgba(0,0,0,0)');
    grd.addColorStop(0.55, 'rgba(0,0,0,0.20)');
    grd.addColorStop(1, 'rgba(0,0,0,0.86)');
    g.fillStyle = grd;
    g.fillRect(0, 0, W, H);
    if (sprites.grain) {
      g.globalAlpha = 0.5;
      for (let y = 0; y < H; y += 128) {
        for (let x = 0; x < W; x += 128) g.drawImage(sprites.grain, x, y);
      }
      g.globalAlpha = 1;
    }
    sprites.vignette = c;
  }

  /* ---------------- meteors ---------------- */

  function seedSparks() {
    sparks = [];
    for (let i = 0; i < 44; i++) sparks.push({
      a: Math.random() * Math.PI * 2,
      r: Math.random(),
      spd: 0.05 + Math.random() * 0.22,
      s: 0.5 + Math.random() * 1.5,
      ph: Math.random() * 6.28,
    });
  }

  function seedSpecks() {
    specks = [];
    for (let i = 0; i < 150; i++) {
      specks.push({
        a: Math.random() * Math.PI * 2,
        u: Math.random(),
        len: 0.01 + Math.random() * 0.05,
        w: Math.random() < 0.8 ? 0.7 : 1.4,
        a0: 0.03 + Math.random() * 0.10,
        ph: Math.random() * 6.28,
      });
    }
  }

  /* ---------------- resize ---------------- */

  function resize() {
    W = window.innerWidth;
    H = window.innerHeight;
    DPR = Math.min(window.devicePixelRatio || 1, 1.25);
    canvas.width = Math.round(W * DPR);
    canvas.height = Math.round(H * DPR);
    canvas.style.width = W + 'px';
    canvas.style.height = H + 'px';
    ctx.setTransform(DPR, 0, 0, DPR, 0, 0);
    makeDust();
    makeVignette();
    makePlates();
    if (!bandSpeeds.length) rollSpeeds();
    buildNebula();
    seedMeteors();
    seedSparks();
    seedSpecks();
  }

  /* ---------------- stats ---------------- */

  function setMusic(pos, dur, playing) {
    music.pos = pos || 0;
    music.dur = dur || 0;
    music.playing = !!playing;
    music.stamp = performance.now();
  }

  function feed(s) {
    if (!s || !s.cpu) return;
    tg.cpu = s.cpu.total || 0;
    tg.gpu = s.gpu && s.gpu.present ? s.gpu.util : 0;
    tg.nd = s.net ? s.net.downBps : 0;
    tg.nu = s.net ? s.net.upBps : 0;
    tg.rd = s.io ? (s.io.readBps || 0) : 0;
    tg.wr = s.io ? (s.io.writeBps || 0) : 0;
    if (s.cpu.cores && s.cpu.cores.length) {
      coreTarget = s.cpu.cores.slice();
      while (cores.length < coreTarget.length) cores.push(0);
    }
  }

  function pt(cx, cy, r, a) {
    return { x: cx + Math.cos(a) * r, y: cy + Math.sin(a) * r };
  }

  /* ---------------- baked plates ---------------- */

  function makePlates() {
    const bg = document.createElement('canvas');
    bg.width = W; bg.height = H;
    const bctx = bg.getContext('2d');
    const g0 = geo();
    const grd = bctx.createRadialGradient(g0.cx, g0.cy, 0, g0.cx, g0.cy, Math.max(W, H) * 0.8);
    grd.addColorStop(0, 'rgba(26,46,68,0.26)');
    grd.addColorStop(0.40, 'rgba(10,19,30,0.20)');
    grd.addColorStop(1, 'rgba(4,6,10,0)');
    bctx.fillStyle = grd;
    bctx.fillRect(0, 0, W, H);
    if (sprites.dust) {
      bctx.globalAlpha = 0.35;
      bctx.drawImage(sprites.dust, 0, 0);
      bctx.globalAlpha = 1;
    }
    sprites.bg = bg;

    const ring = document.createElement('canvas');
    ring.width = W; ring.height = H;
    const rctx = ring.getContext('2d');
    const body = rctx.createRadialGradient(g0.cx, g0.cy, g0.ri, g0.cx, g0.cy, g0.ro);
    body.addColorStop(0, 'rgba(120,170,220,0.03)');
    body.addColorStop(0.35, 'rgba(150,200,245,0.10)');
    body.addColorStop(0.75, 'rgba(170,215,255,0.09)');
    body.addColorStop(1, 'rgba(120,170,220,0.02)');
    rctx.beginPath();
    rctx.arc(g0.cx, g0.cy, g0.ro, ARC.a0, ARC.a1);
    rctx.arc(g0.cx, g0.cy, g0.ri, ARC.a1, ARC.a0, true);
    rctx.fillStyle = body;
    rctx.fill();

    // brushed texture
    rctx.save();
    rctx.translate(g0.cx, g0.cy);
    const hatch = 900;
    for (let i = 0; i < hatch; i++) {
      const a = (i / hatch) * Math.PI * 2;
      const u = Math.random();
      const r = g0.ri + (g0.ro - g0.ri) * (0.06 + u * 0.90);
      const len = 1.5 + Math.random() * 7;
      const alpha = (0.02 + Math.random() * 0.06) * (1 - Math.abs(u - 0.45) * 1.1);
      if (alpha <= 0) continue;
      const c0 = pt(0, 0, r, a);
      const c1 = pt(0, 0, r + len, a);
      rctx.strokeStyle = `rgba(196,226,255,${alpha})`;
      rctx.lineWidth = Math.random() < 0.8 ? 0.6 : 1.1;
      rctx.beginPath();
      rctx.moveTo(c0.x, c0.y);
      rctx.lineTo(c1.x, c1.y);
      rctx.stroke();
    }
    rctx.restore();

    // dark divisions
    rctx.globalCompositeOperation = 'destination-out';
    for (const gap of [0.30, 0.62]) {
      rctx.beginPath();
      rctx.arc(g0.cx, g0.cy, g0.ri + (g0.ro - g0.ri) * gap, 0, Math.PI * 2);
      rctx.lineWidth = (g0.ro - g0.ri) * 0.018;
      rctx.strokeStyle = 'rgba(0,0,0,0.5)';
      rctx.stroke();
    }
    rctx.globalCompositeOperation = 'source-over';

    sprites.body = ring;
  }

  function drawBackground() {
    ctx.clearRect(0, 0, W, H);
    if (sprites.bg) ctx.drawImage(sprites.bg, 0, 0);
  }

  /* ---------------- nebula: the accretion disk ---------------- */

  let nebula = null;
  let nebula2 = null;
  let nebulaSize = 0;
  let spawnAcc = 0;

  function makeNebulaField(size, palette, blobs, rMin, rMax, alphaScale) {
    const c = document.createElement('canvas');
    c.width = c.height = size;
    const g = c.getContext('2d');

    for (let i = 0; i < blobs; i++) {
      const a = Math.random() * Math.PI * 2;
      const u = Math.random();
      const rr = size * (rMin + u * (rMax - rMin));
      const squash = 0.94 + Math.random() * 0.12;
      const x = size / 2 + Math.cos(a) * rr;
      const y = size / 2 + Math.sin(a) * rr * squash;
      const r = size * (0.012 + Math.random() * Math.random() * 0.075);
      const col = palette[(Math.random() * palette.length) | 0];
      const alpha = (0.008 + Math.random() * 0.026) * alphaScale;

      const grd = g.createRadialGradient(x, y, 0, x, y, r);
      grd.addColorStop(0, `rgba(${col[0]},${col[1]},${col[2]},${alpha})`);
      grd.addColorStop(0.45, `rgba(${col[0]},${col[1]},${col[2]},${alpha * 0.30})`);
      grd.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = grd;
      g.beginPath();
      g.arc(x, y, r, 0, Math.PI * 2);
      g.fill();
    }

    // tangential filaments, so the disk reads as rotating gas rather than fog
    for (let i = 0; i < blobs * 0.55; i++) {
      const a = Math.random() * Math.PI * 2;
      const rr = size * (rMin + Math.random() * (rMax - rMin));
      const sweep = 0.10 + Math.random() * 0.42;
      const col = palette[(Math.random() * palette.length) | 0];
      const alpha = (0.006 + Math.random() * 0.018) * alphaScale;
      g.strokeStyle = `rgba(${col[0]},${col[1]},${col[2]},${alpha})`;
      g.lineWidth = size * (0.0015 + Math.random() * 0.004);
      g.beginPath();
      g.arc(size / 2, size / 2, rr, a, a + sweep);
      g.stroke();
    }

    return c;
  }

  function buildNebula() {
    const size = Math.round(Math.max(W, H) * 1.45 / 64) * 64;
    if (nebula && nebulaSize === size) return;
    nebulaSize = size;
    nebula = makeNebulaField(size, [[92, 150, 230], [112, 196, 255], [136, 128, 232], [170, 210, 255]], 620, 0.16, 0.42, 2.6);
    nebula2 = makeNebulaField(size, [[80, 120, 200], [120, 170, 255], [150, 110, 220]], 300, 0.10, 0.30, 2.2);
  }

  function drawNebula() {
    if (!nebula) return;
    const g = geo();
    const S = nebulaSize;
    const energy = 0.55 + Math.min(1, audS.level * 1.6) * 0.55;

    ctx.globalCompositeOperation = 'lighter';

    ctx.save();
    ctx.translate(g.cx, g.cy);
    ctx.rotate(t * 0.010);
    ctx.globalAlpha = Math.min(0.95, 0.78 * energy);
    ctx.drawImage(nebula, -S / 2, -S / 2, S, S);
    ctx.restore();

    // second layer disabled: two full-screen composites doubled the GPU cost

    ctx.globalAlpha = 1;
    ctx.globalCompositeOperation = 'source-over';
  }

  /* ---------------- meteors: one per real data pulse ---------------- */

  function newMeteor() {
    const c = pickClass();
    const g = geo();
    const a = Math.random() * Math.PI * 2;
    const r0 = g.ro * (0.94 + Math.random() * 0.10);
    const bass = (audS.bands[0] + audS.bands[1] + audS.bands[2]) / 3;
    const spd = (280 + Math.random() * 560) * (1 + audS.level * 1.5 + bass * 0.5);
    return {
      cls: c,
      hot: Math.random() < 0.14 + audS.level * 0.15,
      x: g.cx + Math.cos(a) * r0,
      y: g.cy + Math.sin(a) * r0,
      vx: Math.cos(a) * spd,
      vy: Math.sin(a) * spd,
      trail: [],
      maxTrail: 10 + ((Math.random() * 10) | 0),
      w: (0.8 + Math.random() * 1.3) * (1 + bass * 1.1),
      born: t,
    };
  }

  function seedMeteors() {
    meteors = [];
    spawnAcc = 0;
  }

  function drawMeteors(dt) {
    // a pulse of real traffic ejects a meteor; quiet machines stay quiet
    const n = act(CLS[0]) * 1.6 + act(CLS[1]) * 0.8 + act(CLS[2]) * 0.9 + act(CLS[3]) * 0.9 + act(CLS[4]) * 0.6;
    const bass = (audS.bands[0] + audS.bands[1] + audS.bands[2]) / 3;
    const treble = (audS.bands[28] + audS.bands[29] + audS.bands[30] + audS.bands[31]) / 4;

    // loud + bright music simply means more meteors, moving faster
    spawnAcc += (n * 5 + audS.level * 22 + treble * 10) * dt;
    while (spawnAcc >= 1) {
      spawnAcc -= 1;
      if (meteors.length < 90) meteors.push(newMeteor());
    }

    const g = geo();
    const limit = Math.max(W, H) * 0.85;
    ctx.lineCap = 'round';

    for (let i = meteors.length - 1; i >= 0; i--) {
      const m = meteors[i];
      m.x += m.vx * dt;
      m.y += m.vy * dt;
      m.trail.unshift([m.x, m.y]);
      if (m.trail.length > m.maxTrail) m.trail.pop();

      const dx = m.x - g.cx;
      const dy = m.y - g.cy;
      if (Math.hypot(dx, dy) > limit) {
        meteors.splice(i, 1);
        continue;
      }

      const fade = Math.max(0, 1 - Math.hypot(dx, dy) / limit);
      const alpha = (m.hot ? 0.85 : 0.55) * fade;
      const rgb = m.hot ? [255, 255, 255] : m.cls.rgb;
      const [r, gg, bb] = rgb;

      ctx.beginPath();
      ctx.moveTo(m.trail[0][0], m.trail[0][1]);
      for (let s = 1; s < m.trail.length; s++) ctx.lineTo(m.trail[s][0], m.trail[s][1]);
      ctx.strokeStyle = `rgba(${r},${gg},${bb},${alpha * 0.45})`;
      ctx.lineWidth = m.w;
      ctx.stroke();

      const head = Math.min(4, m.trail.length - 1);
      if (head > 0) {
        ctx.beginPath();
        ctx.moveTo(m.trail[0][0], m.trail[0][1]);
        for (let s = 1; s <= head; s++) ctx.lineTo(m.trail[s][0], m.trail[s][1]);
        ctx.strokeStyle = `rgba(${r},${gg},${bb},${alpha})`;
        ctx.lineWidth = m.w * 1.15;
        ctx.stroke();
      }

      const sz = m.hot ? 24 : 14;
      ctx.globalCompositeOperation = 'lighter';
      ctx.globalAlpha = alpha * 0.9;
      ctx.drawImage(sprites.glow, m.x - sz / 2, m.y - sz / 2, sz, sz);
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
    }
  }

  /* wide annulus: concentric Saturn bands + radial core spokes */

  // each layer gets its own random speed (and direction), fixed for the session
  const bandSpeeds = [];
  const ringSpeeds = [];
  function rollSpeeds() {
    bandSpeeds.length = 0;
    ringSpeeds.length = 0;
    for (let i = 0; i < 8; i++) {
      bandSpeeds.push((1.2 + Math.random() * 3.2) * (Math.random() < 0.5 ? -1 : 1));
    }
    for (let i = 0; i < 4; i++) {
      ringSpeeds.push((0.8 + Math.random() * 2.6) * (Math.random() < 0.5 ? -1 : 1));
    }
  }

  const BANDS = [
    { r0: 0.600, r1: 0.645, a: 0.035, dash: [2, 10], spd: 0.030, w: 1 },
    { r0: 0.660, r1: 0.735, a: 0.055, dash: null, spd: 0.018, w: 1 },
    { r0: 0.748, r1: 0.772, a: 0.10, dash: [26, 9], spd: 0.010, w: 1 },
    { r0: 0.786, r1: 0.872, a: 0.045, dash: null, spd: 0.026, w: 1 },
    { r0: 0.884, r1: 0.905, a: 0.13, dash: [1, 4], spd: 0.042, w: 1 },
    { r0: 0.918, r1: 0.968, a: 0.05, dash: [40, 16], spd: 0.014, w: 1 },
    { r0: 0.976, r1: 0.996, a: 0.09, dash: null, spd: 0.008, w: 1 },
  ];

  function inArc(a) {
    const span = ARC.a1 - ARC.a0;
    let d = a - ARC.a0;
    while (d < 0) d += Math.PI * 2;
    while (d > Math.PI * 2) d -= Math.PI * 2;
    return d <= span;
  }

  function drawAnnulus() {
    ctx.lineCap = 'butt';
    const g = geo();
    const load = (sm.cpu * 0.6 + sm.gpu * 0.4) / 100;
    const span = g.ro - g.ri;
    const n = cores.length || 16;
    const seg = (Math.PI * 2) / n;

    /* 1. the matter of the ring (baked plate) */
    if (sprites.body) ctx.drawImage(sprites.body, 0, 0);

    /* 2. per-core cells - brightness only, never plates */
    for (let i = 0; i < n; i++) {
      const loadC = Math.min(1, (coreTarget[i] || 0) / 100);
      const a0 = i * seg + rot + seg * 0.03;
      const a1 = (i + 1) * seg + rot - seg * 0.03;
      if (a1 <= a0) continue;
      if (!inArc(a0) && !inArc(a1)) continue;
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, (g.ri + g.ro) / 2, a0, a1);
      ctx.lineWidth = span * 0.94;
      ctx.strokeStyle = `rgba(${160 + loadC * 70},${205 + loadC * 40},255,${0.018 + loadC * 0.06})`;
      ctx.stroke();
    }

    /* 3. radial dividers between cores */
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const a = i * seg + rot;
      const p0 = pt(g.cx, g.cy, g.ri * 1.01, a);
      const p1 = pt(g.cx, g.cy, g.ro * 0.99, a);
      ctx.moveTo(p0.x, p0.y);
      ctx.lineTo(p1.x, p1.y);
    }
    ctx.lineWidth = 0.8;
    ctx.strokeStyle = `rgba(180,215,250,${0.06 + load * 0.08})`;
    ctx.stroke();

    /* 4. rims */
    ctx.beginPath();
    ctx.arc(g.cx, g.cy, g.ro * 0.998, ARC.a0, ARC.a1);
    ctx.lineWidth = 1.4;
    ctx.strokeStyle = `rgba(198,230,255,${0.14 + load * 0.22})`;
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(g.cx, g.cy, g.ri * 1.004, ARC.a0, ARC.a1);
    ctx.lineWidth = 1.1;
    ctx.strokeStyle = `rgba(198,230,255,${0.09 + load * 0.14})`;
    ctx.stroke();

    /* 5. bands drawn as broken arcs: a ring made of segments, not a solid hoop */
    for (let i = 0; i < BANDS.length; i++) {
      const b = BANDS[i];
      const rMid = g.ri + span * (b.r0 + b.r1) / 2;
      const width = Math.max(0.8, span * (b.r1 - b.r0));

      const segs = 18 + i * 4;
      const gap = 0.22 + (i % 3) * 0.10;
      const phase = rot * (bandSpeeds[i] || 1) + i * 0.7;
      const stepA = (Math.PI * 2) / segs;
      const half = stepA * (1 - gap) * 0.5;

      ctx.lineWidth = width;
      ctx.strokeStyle = `rgba(184,220,255,${b.a * (2.4 + load * 1.6)})`;
      for (let s = 0; s < segs; s++) {
        const a0 = s * stepA + phase - half;
        ctx.beginPath();
        ctx.arc(g.cx, g.cy, rMid, a0, a0 + half * 2);
        ctx.stroke();
      }
    }

    /* 5b. specular sheen sliding across the band */
    const sheenA = (t * 0.16) % (Math.PI * 2);
    for (let k = 0; k < 3; k++) {
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, g.ri + span * (0.35 + k * 0.2), sheenA - 0.5 + k * 0.08, sheenA + 0.5 + k * 0.08);
      ctx.lineWidth = span * (0.22 - k * 0.05);
      ctx.strokeStyle = `rgba(226,244,255,${0.035 - k * 0.008})`;
      ctx.stroke();
    }

    // launch pulse: a bright arc runs along the ring from the clicked sector
    if (pulseT > 0) {
      const a0 = pulseA;
      const a1 = pulseA + 0.85 + (1 - pulseT) * 1.6;
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, (g.ri + g.ro) / 2, a0, a1);
      ctx.lineWidth = span * 0.92;
      ctx.strokeStyle = `rgba(220,246,255,${0.75 * pulseT})`;
      ctx.stroke();

      ctx.beginPath();
      ctx.arc(g.cx, g.cy, (g.ri + g.ro) / 2, a0, a1);
      ctx.lineWidth = span * 1.05;
      ctx.strokeStyle = `rgba(150,220,255,${0.22 * pulseT})`;
      ctx.stroke();
    }

    /* 6. micro panels carried around by the ring */
    ctx.save();
    ctx.translate(g.cx, g.cy);
    ctx.rotate(rot * 0.35);
    for (const s of specks) {
      const r = g.ri + span * (0.22 + s.u * 0.72);
      const half = span * s.len * 0.5;
      const c0 = pt(0, 0, r - half, s.a);
      const c1 = pt(0, 0, r + half, s.a);
      const flick = 0.55 + 0.45 * Math.sin(t * 1.7 + s.ph);
      ctx.strokeStyle = `rgba(205,232,255,${s.a0 * flick * (1.4 + load * 1.5)})`;
      ctx.lineWidth = s.w;
      ctx.beginPath();
      ctx.moveTo(c0.x, c0.y);
      ctx.lineTo(c1.x, c1.y);
      ctx.stroke();
    }
    ctx.restore();

    /* 7. core indices */
    ctx.font = '7px "Cascadia Mono", Consolas, monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (let i = 0; i < n; i++) {
      const am = (i + 0.5) * seg + rot;
      const p = pt(g.cx, g.cy, g.ri * 0.962, am);
      if (p.y > 84 && p.y < H - 300 && inArc(am)) {
        ctx.fillStyle = `rgba(160,190,215,${0.16 + Math.min(1, (coreTarget[i] || 0) / 100) * 0.40})`;
        ctx.fillText(String(i), p.x, p.y);
      }
    }
    ctx.textAlign = 'left';
  }

  function drawWaterfall() {
    const g = geo();
    const n = 190;
    ctx.globalCompositeOperation = 'lighter';
    ctx.lineCap = 'round';

    for (let i = 0; i < n; i++) {
      const band = Math.min(31, Math.floor((i / n) * 32));
      const e = audS.bands[band] || 0;
      const a = (i / n) * Math.PI * 2 + t * 0.018 + Math.sin(i * 2.1) * 0.01;
      const drive = Math.max(0.06, e * 1.35 + audS.level * 0.5);

      const len = g.ro * (0.035 + drive * 0.30) * (0.65 + 0.45 * Math.sin(t * 2.6 + i * 1.7));
      const bend = (0.10 + e * 0.16) * (i % 2 ? 1 : -1);

      const p0 = pt(g.cx, g.cy, g.ro * 0.995, a);
      const p1 = pt(g.cx, g.cy, g.ro + len, a + bend);
      const pc = pt(g.cx, g.cy, g.ro + len * 0.55, a + bend * 0.35);

      const alpha = 0.018 + drive * 0.20;
      const w = 0.6 + e * 1.6;

      ctx.beginPath();
      ctx.moveTo(p0.x, p0.y);
      ctx.quadraticCurveTo(pc.x, pc.y, p1.x, p1.y);
      ctx.strokeStyle = `rgba(190,226,255,${alpha})`;
      ctx.lineWidth = w;
      ctx.stroke();

      // bright root of the stream
      const pr = pt(g.cx, g.cy, g.ro + len * 0.22, a + bend * 0.12);
      ctx.beginPath();
      ctx.moveTo(p0.x, p0.y);
      ctx.quadraticCurveTo((p0.x + pr.x) / 2, (p0.y + pr.y) / 2, pr.x, pr.y);
      ctx.strokeStyle = `rgba(235,248,255,${alpha * 1.5})`;
      ctx.lineWidth = w * 1.2;
      ctx.stroke();
    }

    ctx.globalCompositeOperation = 'source-over';
  }

  function drawLensing() {
    const g = geo();
    const lvl = Math.min(1, audS.level * 1.8);
    ctx.globalCompositeOperation = 'lighter';

    /* photon ring: the bright halo hugging the inner edge */
    ctx.beginPath();
    ctx.arc(g.cx, g.cy, g.ri * 1.045, 0, Math.PI * 2);
    ctx.lineWidth = 2.0 + lvl * 2.6;
    ctx.strokeStyle = `rgba(232,246,255,${0.16 + lvl * 0.40})`;
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(g.cx, g.cy, g.ri * 1.045, 0, Math.PI * 2);
    ctx.lineWidth = 16 + lvl * 14;
    ctx.strokeStyle = `rgba(150,205,255,${0.045 + lvl * 0.10})`;
    ctx.stroke();

    /* gravitational lensing: the disk bent into arcs above and below */
    for (const s of [-1, 1]) {
      const cy2 = g.cy + s * g.ro * 1.42;
      const a0 = s > 0 ? -Math.PI * 0.74 : Math.PI * 0.26;
      const a1 = s > 0 ? -Math.PI * 0.26 : Math.PI * 0.74;

      ctx.beginPath();
      ctx.arc(g.cx, cy2, g.ro * 1.30, a0, a1);
      ctx.lineWidth = 9;
      ctx.strokeStyle = `rgba(168,214,255,${0.035 + lvl * 0.07})`;
      ctx.stroke();

      ctx.beginPath();
      ctx.arc(g.cx, cy2, g.ro * 1.30, a0, a1);
      ctx.lineWidth = 2;
      ctx.strokeStyle = `rgba(214,238,255,${0.06 + lvl * 0.14})`;
      ctx.stroke();
    }

    ctx.globalCompositeOperation = 'source-over';
  }

  function drawBeams(dt) {
    const g = geo();
    const energy = Math.min(1, (sm.nd + sm.nu + sm.rd + sm.wr) / (4e6));
    ctx.save();
    for (let i = 0; i < 7; i++) {
      const a = rot * 0.2 + i * (Math.PI * 2 / 7) + t * 0.01;
      const len = g.ro * (0.18 + energy * 0.55);
      const p0 = pt(g.cx, g.cy, g.ro * 0.99, a);
      const p1 = pt(g.cx, g.cy, g.ro * 0.99 + len, a);
      ctx.beginPath();
      ctx.moveTo(p0.x, p0.y);
      ctx.lineTo(p1.x, p1.y);
      ctx.lineWidth = 1.2;
      ctx.strokeStyle = `rgba(190,225,255,${0.02 + energy * 0.06})`;
      ctx.stroke();
    }
    ctx.restore();
    void dt;
  }

  function drawInnerField() {
    const g = geo();
    const load = (sm.cpu * 0.6 + sm.gpu * 0.4) / 100;

    ctx.save();
    ctx.lineWidth = 0.7;
    const rings = [0.20, 0.34, 0.46];
    for (let i = 0; i < rings.length; i++) {
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, g.ri * rings[i], 0, Math.PI * 2);
      ctx.setLineDash(i % 2 ? [1, 6] : [12, 26]);
      ctx.lineDashOffset = t * (i % 2 ? -6 : 4);
      ctx.strokeStyle = `rgba(150,192,235,${0.03 + load * 0.025})`;
      ctx.stroke();
    }
    ctx.setLineDash([]);
    ctx.beginPath();
    for (let i = 0; i < 24; i++) {
      const a = (i / 24) * Math.PI * 2 + rot * 0.15;
      const p0 = pt(g.cx, g.cy, g.ri * 0.14, a);
      const p1 = pt(g.cx, g.cy, g.ri * 0.95, a);
      ctx.moveTo(p0.x, p0.y);
      ctx.lineTo(p1.x, p1.y);
    }
    ctx.strokeStyle = `rgba(140,180,225,${0.016 + load * 0.012})`;
    ctx.stroke();
    ctx.restore();
  }

  function drawCore(dt) {
    const g = geo();
    const load = (sm.cpu * 0.55 + sm.gpu * 0.45) / 100;
    const R = g.ri;

    // the music is what the core answers to: low bands drive the size, level the fire
    let bass = 0;
    for (let i = 0; i < 6; i++) bass += audS.bands[i] || 0;
    bass = Math.min(1, bass / 4.2);
    const aL = Math.min(1, audS.level * 2.6);

    const pulse = 0.9 + 0.10 * Math.sin(t * 1.25) + load * 0.16
      + aL * 0.20 + bass * 0.22;

    ctx.globalCompositeOperation = 'lighter';

    const halo = R * (1.35 + load * 0.25 + aL * 0.45) * pulse;
    ctx.globalAlpha = Math.min(0.95, 0.20 + load * 0.16 + aL * 0.40);
    ctx.drawImage(sprites.glow, g.cx - halo / 2, g.cy - halo / 2, halo, halo);

    const strands = 9;
    for (let i = 0; i < strands; i++) {
      const k = i / (strands - 1);
      const rr = R * (0.12 + k * 0.78);
      const dir = i % 2 ? -1 : 1;
      const speed = 0.055 + 0.020 * i;
      const a0 = t * speed * dir + i * 1.91;
      const spanA = 1.0 + 0.45 * Math.sin(t * 0.35 + i * 1.3);
      const flick = 0.55 + 0.45 * Math.sin(t * 0.8 + i * 2.1);
      const alpha = (0.05 + load * 0.10) * flick;

      ctx.beginPath();
      ctx.arc(g.cx, g.cy, rr, a0, a0 + spanA);
      ctx.lineWidth = 1.0 - k * 0.5;
      ctx.strokeStyle = `rgba(196,228,255,${Math.max(0.010, alpha)})`;
      ctx.stroke();

      ctx.beginPath();
      ctx.arc(g.cx, g.cy, rr, a0 + Math.PI * 0.9, a0 + Math.PI * 0.9 + spanA * 0.5);
      ctx.strokeStyle = `rgba(168,208,255,${Math.max(0.007, alpha * 0.5)})`;
      ctx.stroke();
    }

    const fil = 40;
    for (let i = 0; i < fil; i++) {
      const a = (i / fil) * Math.PI * 2 + t * 0.055;
      const twinkle = 0.5 + 0.5 * Math.sin(t * 2.3 + i * 2.71);
      const len = R * (0.08 + 0.20 * ((i % 5) / 5)) * (0.5 + 0.5 * twinkle) * (0.65 + load);
      const p0 = pt(g.cx, g.cy, R * 0.05, a);
      const p1 = pt(g.cx, g.cy, R * 0.05 + len, a);
      ctx.beginPath();
      ctx.moveTo(p0.x, p0.y);
      ctx.lineTo(p1.x, p1.y);
      ctx.lineWidth = 0.7;
      ctx.strokeStyle = `rgba(210,235,255,${0.03 + 0.12 * twinkle * (0.35 + load)})`;
      ctx.stroke();
    }

    const nucleus = R * (0.58 + aL * 0.30) * pulse;
    ctx.globalAlpha = 1;
    ctx.drawImage(sprites.glow, g.cx - nucleus / 2, g.cy - nucleus / 2, nucleus, nucleus);

    const hot = R * 0.12 * (1 + load * 0.35 + aL * 0.9 + bass * 0.7);
    ctx.shadowColor = aL > 0.05 ? `rgba(255,236,246,${Math.min(0.95, aL)})` : 'rgba(0,0,0,0)';
    ctx.shadowBlur = 20 * aL;
    ctx.drawImage(sprites.glow, g.cx - hot, g.cy - hot, hot * 2, hot * 2);
    ctx.shadowBlur = 0;

    // a visible ring that swells with the bass - the clearest music cue in the core
    if (aL > 0.02) {
      const beat = R * (0.30 + bass * 0.34 + aL * 0.10);
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, beat, 0, Math.PI * 2);
      ctx.lineWidth = 1.5 + aL * 2.5;
      ctx.strokeStyle = `rgba(226,246,255,${Math.min(0.95, 0.15 + aL * 0.75)})`;
      ctx.stroke();

      ctx.beginPath();
      ctx.arc(g.cx, g.cy, beat * 1.22, 0, Math.PI * 2);
      ctx.lineWidth = 1;
      ctx.strokeStyle = `rgba(255,196,226,${Math.min(0.8, aL * 0.55)})`;
      ctx.stroke();
    }

    const sw = R * (1.7 + load * 0.5);
    const sh = R * 0.10;
    ctx.globalAlpha = 0.24 + load * 0.16;
    ctx.drawImage(sprites.streak, g.cx - sw / 2, g.cy - sh / 2, sw, sh);

    ctx.save();
    ctx.translate(g.cx, g.cy);
    ctx.rotate(Math.PI / 2);
    ctx.globalAlpha = 0.07 + load * 0.06;
    ctx.drawImage(sprites.streak, -R * 0.7, -R * 0.05, R * 1.4, R * 0.10);
    ctx.restore();

    for (const s of sparks) {
      s.r += dt * s.spd * 0.10;
      if (s.r > 1) s.r -= 1;
      const a = s.a + t * 0.045;
      const rr = R * (0.16 + s.r * 0.9);
      const x = g.cx + Math.cos(a) * rr;
      const y = g.cy + Math.sin(a) * rr * 0.94;
      const al = Math.sin(s.r * Math.PI) * 0.5 * (0.6 + 0.4 * Math.sin(t * 3 + s.ph));
      ctx.globalAlpha = Math.max(0, al);
      ctx.fillStyle = '#cfeaff';
      ctx.fillRect(x, y, s.s, s.s);
    }

    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 1;
  }

  function drawVignette() {
    if (!sprites.vignette) return;
    ctx.globalAlpha = 0.95;
    ctx.drawImage(sprites.vignette, 0, 0);
    ctx.globalAlpha = 1;
  }

  /* ---------------- desktop shortcuts on an outer orbit ---------------- */

  function feedIcons(list) {
    if (!Array.isArray(list) || !list.length) return;
    iconRing = [];
    for (const it of list) {
      let img = null;
      if (it.png) {
        img = new Image();
        img.src = 'data:image/png;base64,' + it.png;
      }
      iconRing.push({ name: it.name || '', path: it.path || '', img });
    }
  }

  function drawIcons() {
    const n = statusItems.length;
    if (!n) return;

    const bw = 232;
    const bh = 52;
    const gap = 9;
    const x = W - bw - 18;
    let y = Math.round(H * 0.5 - (n * bh + (n - 1) * gap) / 2);

    for (let i = 0; i < n; i++) {
      const it = statusItems[i];
      const pct = Math.max(0, Math.min(1, (it.pct || 0) / 100));

      ctx.fillStyle = 'rgba(5,9,14,0.72)';
      ctx.fillRect(x, y, bw, bh);
      ctx.strokeStyle = 'rgba(143,230,255,0.10)';
      ctx.lineWidth = 1;
      ctx.strokeRect(x + 0.5, y + 0.5, bw - 1, bh - 1);

      ctx.fillStyle = pct > 0.8 ? 'rgba(255,120,120,0.85)'
        : pct > 0.6 ? 'rgba(255,208,140,0.85)'
          : 'rgba(143,230,255,0.75)';
      ctx.fillRect(x, y, 2, bh);

      ctx.textAlign = 'left';
      ctx.font = '7px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = 'rgba(140,170,195,0.85)';
      ctx.fillText(it.label || '', x + 12, y + 18);

      ctx.font = '17px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = 'rgba(232,246,255,0.95)';
      ctx.fillText(it.value || '', x + 12, y + 40);

      ctx.fillStyle = 'rgba(255,255,255,0.08)';
      ctx.fillRect(x + 92, y + 34, bw - 108, 3);
      ctx.fillStyle = pct > 0.8 ? 'rgba(255,120,120,0.95)'
        : pct > 0.6 ? 'rgba(255,208,140,0.95)'
          : 'rgba(150,225,255,0.95)';
      ctx.fillRect(x + 92, y + 34, (bw - 108) * pct, 3);

      y += bh + gap;
    }

    ctx.textAlign = 'left';
  }

  function pt(cx, cy, r, a) {
    return { x: cx + Math.cos(a) * r, y: cy + Math.sin(a) * r };
  }

  /* ---------------- running windows, just inside the ring ---------------- */

  function feedWindows(list) {
    if (!Array.isArray(list)) return;
    winCards = list.slice(0, 6).map((it) => {
      let img = null;
      if (it.icon) {
        img = new Image();
        img.src = 'data:image/png;base64,' + it.icon;
      }
      let shot = null;
      if (it.png) {
        shot = new Image();
        shot.src = 'data:image/png;base64,' + it.png;
      }
      return { title: it.title || '', proc: it.proc || '', img, shot };
    });
  }

  function drawWindows() {
    const n = winCards.length;
    if (!n) return;
    const g = geo();
    const R = g.ri * 0.80;
    const step = (Math.PI * 2) / n;
    const cw = 116;

    for (let i = 0; i < n; i++) {
      const a = i * step + rot * 0.55;
      const x = g.cx + Math.cos(a) * R;
      const y = g.cy + Math.sin(a) * R;
      if (y < 96 || y > H - 320) continue;

      const card = winCards[i];
      const hasShot = card.shot && card.shot.complete && card.shot.naturalWidth > 0;
      const ch = hasShot ? 84 : 34;

      ctx.save();
      ctx.translate(x - cw / 2, y - ch / 2);

      ctx.fillStyle = 'rgba(6,10,15,0.78)';
      ctx.strokeStyle = 'rgba(160,205,240,0.22)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.rect(0, 0, cw, ch);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = 'rgba(143,230,255,0.55)';
      ctx.fillRect(0, 0, 2, ch);

      if (hasShot) {
        ctx.globalAlpha = 0.92;
        ctx.drawImage(card.shot, 3, 3, cw - 6, ch - 25);
        ctx.globalAlpha = 1;
      } else {
        let tx0 = 8;
        if (card.img && card.img.complete && card.img.naturalWidth > 0) {
          ctx.drawImage(card.img, 8, 8, 16, 16);
          tx0 = 30;
        }
        ctx.font = '7.5px "Cascadia Mono", Consolas, monospace';
        ctx.fillStyle = 'rgba(214,230,244,0.92)';
        ctx.fillText((card.proc || 'window').slice(0, 14), tx0, 15);

        ctx.font = '6px "Cascadia Mono", Consolas, monospace';
        ctx.fillStyle = 'rgba(120,140,158,0.85)';
        ctx.fillText((card.title || '').slice(0, 22), tx0, 26);
      }

      if (hasShot) {
        ctx.font = '6.5px "Cascadia Mono", Consolas, monospace';
        ctx.fillStyle = 'rgba(214,230,244,0.9)';
        const title = card.title.length > 20 ? card.title.slice(0, 19) + '..' : card.title;
        ctx.fillText(title, 5, ch - 12);
        ctx.font = '6px "Cascadia Mono", Consolas, monospace';
        ctx.fillStyle = 'rgba(120,140,158,0.8)';
        ctx.fillText((card.proc || '').slice(0, 20), 5, ch - 4);
      }

      ctx.restore();
    }
  }

  /* ---------------- player satellite ---------------- */

  let audioOn = false;
  let satHit = null;

  function setAudio(on) {
    audioOn = on;
  }

  function drawStatusCard(px, py, it, pct, r, gg, b) {
    const cw = 96;
    const ch = 40;
    let bx = px + 16;
    let by = py - ch / 2;
    if (bx + cw > W - 12) bx = px - cw - 16;
    if (by < 10) by = 10;

    ctx.fillStyle = 'rgba(6,11,16,0.90)';
    ctx.fillRect(bx, by, cw, ch);
    ctx.strokeStyle = `rgba(${r},${gg},${b},0.55)`;
    ctx.lineWidth = 1;
    ctx.strokeRect(bx + 0.5, by + 0.5, cw - 1, ch - 1);

    ctx.textAlign = 'left';
    ctx.font = '7px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = 'rgba(150,180,205,0.9)';
    ctx.fillText(it.label || '', bx + 7, by + 12);

    ctx.font = '13px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = `rgba(${r},${gg},${b},0.98)`;
    ctx.fillText(it.value || '', bx + 7, by + 27);

    ctx.fillStyle = 'rgba(255,255,255,0.10)';
    ctx.fillRect(bx + 7, by + 32, cw - 14, 3);
    ctx.fillStyle = `rgba(${r},${gg},${b},0.9)`;
    ctx.fillRect(bx + 7, by + 32, (cw - 14) * pct, 3);
  }

  function drawDetail() {
    if (!detailOn || !statusItems.length) return;

    const w = 372;
    const h = 250;
    const x = Math.round(W * 0.5 - w / 2);
    const y = Math.round(H * 0.5 - h / 2);

    ctx.fillStyle = 'rgba(5,9,14,0.92)';
    ctx.fillRect(x, y, w, h);
    ctx.strokeStyle = 'rgba(143,230,255,0.35)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);

    ctx.textAlign = 'left';
    ctx.font = '9px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = 'rgba(150,180,205,0.9)';
    ctx.fillText('SYSTEM DETAIL', x + 16, y + 24);
    ctx.textAlign = 'right';
    ctx.fillStyle = 'rgba(120,145,168,0.7)';
    ctx.fillText('CLICK RING TO CLOSE', x + w - 16, y + 24);
    ctx.textAlign = 'left';

    ctx.strokeStyle = 'rgba(255,255,255,0.10)';
    ctx.beginPath();
    ctx.moveTo(x + 16, y + 32);
    ctx.lineTo(x + w - 16, y + 32);
    ctx.stroke();

    for (let i = 0; i < statusItems.length; i++) {
      const it = statusItems[i];
      const col = i % 2;
      const row = (i / 2) | 0;
      const cx = x + 16 + col * (w / 2 - 8);
      const cy = y + 58 + row * 46;

      ctx.font = '8px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = 'rgba(140,168,190,0.85)';
      ctx.fillText(it.label || '', cx, cy);

      ctx.font = '16px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = 'rgba(228,246,255,0.96)';
      ctx.fillText(it.value || '', cx, cy + 20);

      const pct = Math.max(0, Math.min(1, (it.pct || 0) / 100));
      const bw = w / 2 - 34;
      ctx.fillStyle = 'rgba(255,255,255,0.10)';
      ctx.fillRect(cx, cy + 26, bw, 3);
      ctx.fillStyle = pct > 0.8 ? 'rgba(255,120,120,0.95)' : pct > 0.6 ? 'rgba(255,208,140,0.95)' : 'rgba(150,225,255,0.95)';
      ctx.fillRect(cx, cy + 26, bw * pct, 3);
    }

    ctx.textAlign = 'left';
  }

  function drawStatusSegments() {
    if (!statusItems.length) return;
    const g = geo();
    const R = g.ro * 0.94;
    const n = statusItems.length;
    const step = (Math.PI * 2) / n;
    const half = step * 0.28;

    for (let i = 0; i < n; i++) {
      const it = statusItems[i];
      const a = Math.PI * 0.70 + (i / Math.max(1, n - 1)) * Math.PI * 0.44 + rot * 0.35;
      const pct = Math.max(0, Math.min(1, (it.pct || 0) / 100));

      const r = Math.round(120 + pct * 120);
      const gg = Math.round(220 - pct * 120);
      const b = Math.round(255 - pct * 140);

      ctx.beginPath();
      ctx.arc(g.cx, g.cy, R, a - half, a + half);
      ctx.lineWidth = 4.5;
      ctx.strokeStyle = `rgba(${r},${gg},${b},${0.28 + pct * 0.55})`;
      ctx.stroke();

      const px = g.cx + Math.cos(a) * (R + 12);
      const py = g.cy + Math.sin(a) * (R + 12);
      if (py < 46 || py > H - 46 || px < 40 || px > W - 40) continue;



      const flip = Math.cos(a) < 0;
      ctx.save();
      ctx.translate(px, py);
      ctx.textAlign = 'center';
      ctx.font = '6.5px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = 'rgba(150,180,205,0.85)';
      ctx.fillText(it.label || '', 0, 0);
      ctx.font = '8px "Cascadia Mono", Consolas, monospace';
      ctx.fillStyle = `rgba(${r},${gg},${b},0.95)`;
      ctx.fillText(it.value || '', 0, 11);
      ctx.restore();
      ctx.textAlign = 'left';
    }
  }

  const ROMAN = ['XII', 'I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X', 'XI'];

  function drawClock() {
    const g = geo();
    const R = g.ro * 1.10;
    const now = new Date();
    const hour = now.getHours() % 12;

    for (let i = 0; i < 12; i++) {
      // twelve numerals spread over the lower break of the ring
      const a = Math.PI * 0.22 + (i / 11) * Math.PI * 0.56;
      const x = g.cx + Math.cos(a) * R;
      const y = g.cy + Math.sin(a) * R;
      if (x < 26 || x > W - 26 || y < 70 || y > H - 26) continue;

      const active = i === hour;
      ctx.font = `${active ? 12 : 9}px "Cascadia Mono", Consolas, monospace`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      if (active) {
        ctx.shadowColor = 'rgba(150,232,255,0.95)';
        ctx.shadowBlur = 14;
      }
      ctx.fillStyle = active ? 'rgba(228,249,255,0.98)' : 'rgba(148,178,204,0.42)';
      ctx.fillText(ROMAN[i], x, y);
      ctx.shadowBlur = 0;
    }

    const a0 = Math.PI * 0.22 + (hour / 11) * Math.PI * 0.56 - Math.PI / 22;
    ctx.beginPath();
    ctx.arc(g.cx, g.cy, R, a0, a0 + Math.PI / 11);
    ctx.lineWidth = 2.4;
    ctx.strokeStyle = 'rgba(160,235,255,0.50)';
    ctx.stroke();

    ctx.textAlign = 'left';
    ctx.textBaseline = 'alphabetic';
  }

  function drawSatellite() {
    const g = geo();
    const a = -t * 0.24;
    const R = g.ro * 0.985;
    const x = g.cx + Math.cos(a) * R;
    const y = g.cy + Math.sin(a) * R;

    for (let i = 1; i <= 8; i++) {
      const pa = a + i * 0.018;
      const px = g.cx + Math.cos(pa) * R;
      const py = g.cy + Math.sin(pa) * R;
      ctx.beginPath();
      ctx.moveTo(px, py);
      ctx.lineTo(px + Math.cos(pa) * 2, py + Math.sin(pa) * 2);
      ctx.strokeStyle = `rgba(150,215,255,${0.24 * (1 - i / 9)})`;
      ctx.lineWidth = 1.2;
      ctx.stroke();
    }

    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(a + Math.PI / 2);

    ctx.fillStyle = audioOn ? 'rgba(120,200,255,0.55)' : 'rgba(120,150,180,0.35)';
    ctx.fillRect(-17, -4.2, 12, 8.4);
    ctx.fillRect(5, -4.2, 12, 8.4);
    ctx.strokeStyle = 'rgba(180,215,245,0.5)';
    ctx.lineWidth = 0.6;
    ctx.strokeRect(-17, -4.2, 12, 8.4);
    ctx.strokeRect(5, -4.2, 12, 8.4);

    ctx.fillStyle = audioOn ? 'rgba(210,240,255,0.95)' : 'rgba(190,210,230,0.8)';
    ctx.fillRect(-5, -5, 10, 10);

    ctx.beginPath();
    ctx.arc(0, -8, 4, Math.PI, Math.PI * 2);
    ctx.strokeStyle = audioOn ? 'rgba(200,240,255,0.95)' : 'rgba(180,200,220,0.7)';
    ctx.lineWidth = 1;
    ctx.stroke();

    if (audioOn) {
      ctx.globalCompositeOperation = 'lighter';
      ctx.globalAlpha = 0.55 + 0.35 * Math.sin(t * 6);
      ctx.drawImage(sprites.glow, -28, -28, 56, 56);
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
    }

    ctx.restore();

    ctx.font = '6.5px "Cascadia Mono", Consolas, monospace';
    ctx.textAlign = 'center';
    ctx.fillStyle = audioOn ? 'rgba(180,240,255,0.9)' : 'rgba(150,175,195,0.7)';
    ctx.fillText(audioOn ? 'PAUSE' : 'PLAY', x, y + 20);
    ctx.textAlign = 'left';

    satHit = { x: x - 24, y: y - 24, w: 48, h: 48 };
  }

  /* ---------------- loop ---------------- */

  function frame(ts) {
    if (!running) return;
    window.__hfFrames = (window.__hfFrames | 0) + 1;

    if (ts - lastFrame < 30) {
      rafId = requestAnimationFrame(frame);
      return;
    }

    hitBoxes = [];
    const dt = Math.min(0.08, (ts - lastFrame) / 1000 || 0.033);
    lastFrame = ts;
    t += dt;

    const k = Math.min(1, dt * 2.6);
    sm.cpu += (tg.cpu - sm.cpu) * k;
    sm.gpu += (tg.gpu - sm.gpu) * k;
    sm.nd += (tg.nd - sm.nd) * k;
    sm.nu += (tg.nu - sm.nu) * k;
    sm.rd += (tg.rd - sm.rd) * k;
    sm.wr += (tg.wr - sm.wr) * k;
    for (let i = 0; i < cores.length; i++) {
      cores[i] += ((coreTarget[i] || 0) - cores[i]) * k;
    }

    // the ring turns faster the harder the machine works, but eases so it never jumps
    const cpuLoad = Math.min(1, sm.cpu / 100);
    rotBoost += (cpuLoad - rotBoost) * Math.min(1, dt * 1.1);
    rot += dt * (0.012 + rotBoost * 0.24);
    if (pulseT > 0) pulseT = Math.max(0, pulseT - dt * 0.75);

    audS.level += (aud.level - audS.level) * Math.min(1, dt * 9);
    for (let i = 0; i < 32; i++) {
      audS.bands[i] += ((aud.bands[i] || 0) - audS.bands[i]) * Math.min(1, dt * 12);
    }

    drawBackground();
    drawNebula();
    drawAnnulus();
    drawLensing();
    drawBeams(dt);

    drawInnerField();
    drawCore(dt);
    drawMeteors(dt);
    drawDetail();
    drawStatusSegments();
    drawSectorPanels();
    drawArcMeters();
    drawMusicArc();
    drawSatellite();
    // window previews are native DWM thumbnails now
    drawVignette();

    rafId = requestAnimationFrame(frame);
  }

  function start() {
    if (running && rafId) return;
    running = true;
    lastFrame = performance.now();
    rafId = requestAnimationFrame(frame);
  }

  function stop() {
    running = false;
    if (rafId) cancelAnimationFrame(rafId);
    rafId = 0;
  }

  function init() {
    canvas = document.getElementById('stage');
    ctx = canvas.getContext('2d', { alpha: true });
    makeSprites();
    resize();
    window.addEventListener('resize', () => resize());

    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener('message', (e) => {
        if (e.data === 'pause') stop();
        else if (e.data === 'resume') start();
      });
    }

    start();
  }

  let pulseA = -999;
  let pulseT = 0;

  /* ---------- sector panels: the eight readouts bent around the ring ---------- */
  let sectorPanels = [];

  function setPanels(list) {
    if (Array.isArray(list)) sectorPanels = list.slice(0, 8);
  }

  function drawArcText(text, cx, cy, r, aStart, step, color, size, alignRight) {
    const chars = Array.from(String(text == null ? '' : text));
    if (!chars.length) return;
    ctx.font = size + 'px "Cascadia Mono", Consolas, monospace';
    ctx.fillStyle = color;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    // on the lower half the glyphs would be upside down: walk the arc backwards
    const mid = aStart + step * chars.length * 0.5;
    const flip = Math.sin(mid) > 0;
    let a = alignRight ? aStart - step * chars.length : aStart;
    const order = flip ? chars.slice().reverse() : chars;

    for (let i = 0; i < order.length; i++) {
      ctx.save();
      ctx.translate(cx + Math.cos(a) * r, cy + Math.sin(a) * r);
      ctx.rotate(a + Math.PI / 2 + (flip ? Math.PI : 0));
      ctx.fillText(order[i], 0, 0);
      ctx.restore();
      a += step;
    }
    ctx.textAlign = 'left';
    ctx.textBaseline = 'alphabetic';
  }

  /* ---------- data arcs: one per sector, along its inner edge ---------- */
  function drawArcMeters() {
    if (!sectorPanels.length) return;

    const g = geo();
    const n = sectorPanels.length;
    const span = (Math.PI * 2) / n;
    const pad = 0.035;
    const drift = rot * 0.18;

    while (arcMeters.length < n) arcMeters.push({ shown: 0, target: 0, flash: 0, seed: Math.random() });

    for (let i = 0; i < n; i++) {
      const p = sectorPanels[i];
      const m = arcMeters[i];

      const a0 = i * span - Math.PI / 2 + drift + pad;
      const a1 = a0 + span - pad * 2;
      const usable = a1 - a0;

      // a panel's headline is its first row that carries a real percentage
      const row = (p.rows || []).find(r => typeof r.pct === 'number' && r.pct > 0);
      const pct = row ? Math.max(0, Math.min(1, row.pct / 100)) : 0;

      // remember the previous value so a real change can be announced
      if (!m.init) { m.init = true; m.shown = pct; m.target = pct; }
      if (Math.abs(pct - m.target) > 0.04) m.flash = 1;
      m.target = pct;
      m.shown += (m.target - m.shown) * 0.12;
      m.flash = Math.max(0, m.flash - 0.05);

      // a slow breath keeps the arc alive even when the value is flat
      const breath = 1 + Math.sin(t * 0.55 + m.seed * 6.283) * 0.012;
      const shown = Math.max(0, Math.min(1, m.shown * breath));

      const r = g.ri + 9;
      const track0 = a0 + usable * 0.10;
      const track1 = a1 - usable * 0.10;
      const trackLen = track1 - track0;

      // track
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, r, track0, track1);
      ctx.lineWidth = 2.4;
      ctx.strokeStyle = 'rgba(150,190,220,0.10)';
      ctx.stroke();

      // fill
      const fillEnd = track0 + trackLen * shown;
      const hot = m.flash;
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, r, track0, fillEnd);
      ctx.lineWidth = 2.6 + hot * 1.6;
      ctx.strokeStyle = hot > 0.02
        ? `rgba(255,${238 - hot * 30},${214 - hot * 50},${0.55 + hot * 0.4})`
        : `rgba(${160 + hot * 60},228,255,${0.62 + hot * 0.3})`;
      ctx.stroke();

      // the tip: a small comet head so movement is obvious
      if (shown > 0.01) {
        const tip = pt(g.cx, g.cy, r, fillEnd);
        ctx.beginPath();
        ctx.arc(tip.x, tip.y, 2.1 + hot * 1.4, 0, Math.PI * 2);
        ctx.fillStyle = `rgba(235,250,255,${0.75 + hot * 0.25})`;
        ctx.fill();

        ctx.beginPath();
        ctx.arc(tip.x, tip.y, 5.5 + hot * 4, 0, Math.PI * 2);
        const glow = ctx.createRadialGradient(tip.x, tip.y, 0, tip.x, tip.y, 6 + hot * 5);
        glow.addColorStop(0, `rgba(190,235,255,${0.5 + hot * 0.4})`);
        glow.addColorStop(1, 'rgba(190,235,255,0)');
        ctx.fillStyle = glow;
        ctx.fill();
      }
    }
  }

  /* ---------- music progress: a thin arc on the inner rim ---------- */
  function drawMusicArc() {
    if (!music.dur) return;

    const g = geo();
    const elapsed = music.playing ? music.pos + (performance.now() - music.stamp) / 1000 : music.pos;
    const frac = Math.max(0, Math.min(1, elapsed / music.dur));

    const r = g.ri * 0.90;
    const start = -Math.PI / 2;

    // track
    ctx.beginPath();
    ctx.arc(g.cx, g.cy, r, 0, Math.PI * 2);
    ctx.lineWidth = 2.4;
    ctx.strokeStyle = 'rgba(150,190,220,0.10)';
    ctx.stroke();

    // start marker at twelve o'clock
    const top = pt(g.cx, g.cy, r, start);
    ctx.beginPath();
    ctx.arc(top.x, top.y, 2.2, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(210,235,255,0.55)';
    ctx.fill();

    if (frac <= 0.001) return;

    ctx.beginPath();
    ctx.arc(g.cx, g.cy, r, start, start + Math.PI * 2 * frac);
    ctx.lineWidth = 3.2;
    ctx.strokeStyle = music.playing ? 'rgba(205,245,255,0.92)' : 'rgba(150,190,220,0.40)';
    ctx.stroke();

    const tip = pt(g.cx, g.cy, r, start + Math.PI * 2 * frac);
    ctx.beginPath();
    ctx.arc(tip.x, tip.y, 3.4, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(240,252,255,0.98)';
    ctx.fill();

    ctx.beginPath();
    ctx.arc(tip.x, tip.y, 8, 0, Math.PI * 2);
    const halo = ctx.createRadialGradient(tip.x, tip.y, 0, tip.x, tip.y, 8);
    halo.addColorStop(0, 'rgba(200,240,255,0.45)');
    halo.addColorStop(1, 'rgba(200,240,255,0)');
    ctx.fillStyle = halo;
    ctx.fill();
  }

  function drawSectorPanels() {
    if (!sectorPanels.length) return;
    const g = geo();
    const n = sectorPanels.length;
    const span = (Math.PI * 2) / n;
    const pad = 0.035;
    const drift = rot * 0.18;

    for (let i = 0; i < n; i++) {
      const p = sectorPanels[i];
      const a0 = i * span - Math.PI / 2 + drift + pad;
      const a1 = a0 + span - pad * 2;
      const mid = (a0 + a1) / 2;

      // the sector body
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, g.ro, a0, a1);
      ctx.arc(g.cx, g.cy, g.ri, a1, a0, true);
      ctx.closePath();
      ctx.fillStyle = 'rgba(5,9,14,0.52)';
      ctx.fill();
      ctx.strokeStyle = 'rgba(143,230,255,0.16)';
      ctx.lineWidth = 1;
      ctx.stroke();

      // title along the outer edge, tag at the other end
      drawArcText(p.title || '', g.cx, g.cy, g.ro - 11, a0 + 0.07, 0.056, 'rgba(176,206,230,1.0)', 10, false);
      if (p.tag) drawArcText(p.tag, g.cx, g.cy, g.ro - 11, a1 - 0.07, 0.046, 'rgba(126,156,180,0.8)', 8, true);

      // divider
      ctx.beginPath();
      ctx.arc(g.cx, g.cy, g.ro - 20, a0 + 0.05, a1 - 0.05);
      ctx.strokeStyle = 'rgba(255,255,255,0.07)';
      ctx.lineWidth = 1;
      ctx.stroke();

      const rows = p.rows || [];
      const rTop = g.ro - 36;
      const rBottom = g.ri + 16;
      const stepR = rows.length > 1 ? (rTop - rBottom) / (rows.length - 1) : 0;

      for (let j = 0; j < rows.length; j++) {
        const row = rows[j];
        const rr = rTop - stepR * j;

        drawArcText(row.label || '', g.cx, g.cy, rr, a0 + 0.10, 0.050, 'rgba(158,188,212,0.95)', 8, false);
        drawArcText(row.value || '', g.cx, g.cy, rr, a1 - 0.10, 0.052, 'rgba(240,250,255,1.0)', 9, true);

      }
    }
  }

  let statusItems = [];

  let hitBoxes = [];
  let cursorX = -1;
  let cursorY = -1;
  let hoverIndex = -1;
  let hoverId = null;

  function flashAt(angle) {
    pulseA = angle;
    pulseT = 1;
  }

  let detailOn = false;

  function toggleDetail() {
    detailOn = !detailOn;
  }

  function setStatus(list) {
    if (Array.isArray(list)) statusItems = list.slice(0, 8);
  }

  function setCursor(x, y, over) {
    hoverId = null;
    hoverIndex = -1;
    cursorX = over ? x : -1;
    cursorY = over ? y : -1;
    if (!over) return;
    for (let i = 0; i < hitBoxes.length; i++) {
      const b = hitBoxes[i];
      if (x >= b.x && x <= b.x + b.w && y >= b.y && y <= b.y + b.h) {
        hoverIndex = i;
        hoverId = b.id || null;
        return;
      }
    }
  }

  function hits() {
    const list = hitBoxes
      .filter((b) => b.action)
      .map((b) => ({ x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.w), h: Math.round(b.h), action: b.action }));
    if (satHit) list.push({ x: Math.round(satHit.x), y: Math.round(satHit.y), w: satHit.w, h: satHit.h, action: 'audio' });
    return list;
  }

  function feedAudio(f) {
    if (!f) return;
    aud.level = f.level || 0;
    if (f.bands) {
      for (let i = 0; i < aud.bands.length && i < f.bands.length; i++) {
        aud.bands[i] = f.bands[i] || 0;
      }
    }
  }

  function ringAngle() { return rot; }

  return { init, feed, setMusic, feedAudio, feedIcons, feedWindows, setCursor, hits, setStatus, setPanels, toggleDetail, setAudio, flashAt, ringAngle, start, stop, resize };
})();

window.HFX = HFX;
window.addEventListener('DOMContentLoaded', HFX.init);

