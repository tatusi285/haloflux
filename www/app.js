/* HALOFLUX desktop - binds the live telemetry feed to the HUD. */

(() => {
  const $ = (id) => document.getElementById(id);

  const state = {
    last: null,
    lastOk: 0,
    histNet: [],
    histIo: [],
  };

  const DASH = '--';
  const DAY_NAMES = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];

  /* ---------------- formatting ---------------- */

  const fmtRate = (bps) => {
    if (!isFinite(bps) || bps <= 0) return '0 B';
    if (bps >= 1048576) return (bps / 1048576).toFixed(1) + ' MB';
    if (bps >= 1024) return (bps / 1024).toFixed(0) + ' KB';
    return bps.toFixed(0) + ' B';
  };

  const fmtShort = (bps) => {
    if (!bps || bps <= 0) return '0';
    if (bps >= 1048576) return (bps / 1048576).toFixed(1) + 'M';
    if (bps >= 1024) return (bps / 1024).toFixed(0) + 'K';
    return bps.toFixed(0) + 'B';
  };

  const fmtDur = (sec) => {
    const d = Math.floor(sec / 86400);
    const h = Math.floor((sec % 86400) / 3600);
    const m = Math.floor((sec % 3600) / 60);
    if (d > 0) return `${d}d ${h}h`;
    if (h > 0) return `${h}h ${m}m`;
    return `${m}m`;
  };

  const clampPct = (v) => Math.max(0, Math.min(100, v || 0));

  /* ---------------- rows ---------------- */

  function renderRows(host, items) {
    while (host.children.length < items.length) {
      const row = document.createElement('div');
      row.className = 'row';
      row.innerHTML = '<span class="n"></span><span class="bar"><i></i></span><span class="v"></span>';
      host.appendChild(row);
    }
    for (let i = 0; i < host.children.length; i++) {
      const row = host.children[i];
      const item = items[i];
      if (!item) {
        row.style.display = 'none';
        continue;
      }
      row.style.display = '';
      row.classList.toggle('dim', !!item.dim);
      row.querySelector('.n').textContent = item.name;
      row.querySelector('.bar i').style.width = clampPct(item.pct) + '%';
      row.querySelector('.v').textContent = item.val;
    }
  }

  /* ---------------- canvas charts ---------------- */

  function prep(canvas) {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = canvas.clientWidth || 100;
    const h = canvas.clientHeight || 24;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const g = canvas.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    return { g, w, h };
  }

  function spark(canvas, values, hi, color) {
    const { g, w, h } = prep(canvas);

    g.strokeStyle = 'rgba(255,255,255,0.06)';
    g.lineWidth = 1;
    for (let i = 1; i < 4; i++) {
      const y = (h / 4) * i + 0.5;
      g.beginPath();
      g.moveTo(0, y);
      g.lineTo(w, y);
      g.stroke();
    }

    if (!values.length) return;
    const span = Math.max(1e-6, hi);
    g.beginPath();
    for (let i = 0; i < values.length; i++) {
      const x = (i / Math.max(1, values.length - 1)) * w;
      const y = h - (Math.max(0, values[i]) / span) * (h - 2) - 1;
      if (i === 0) g.moveTo(x, y);
      else g.lineTo(x, y);
    }
    g.strokeStyle = color || 'rgba(143,230,255,0.85)';
    g.lineWidth = 1.2;
    g.stroke();
  }

  function weekChart(canvas, hist) {
    const { g, w, h } = prep(canvas);
    if (!hist || !hist.length) return;

    const base = h - 9;
    const cell = w / hist.length;
    const barW = Math.max(3, Math.min(13, cell * 0.45));
    const todayKey = new Date().toISOString().slice(0, 10);

    g.strokeStyle = 'rgba(255,255,255,0.08)';
    g.lineWidth = 1;
    g.beginPath();
    g.moveTo(0, base + 0.5);
    g.lineTo(w, base + 0.5);
    g.stroke();

    g.font = '6.5px "Cascadia Mono", Consolas, monospace';
    g.textAlign = 'center';
    g.textBaseline = 'alphabetic';

    for (let i = 0; i < hist.length; i++) {
      const d = hist[i];
      const dt = new Date(d.d + 'T00:00:00');
      const label = DAY_NAMES[dt.getDay()].slice(0, 2);
      const today = d.d === todayKey;
      const v = clampPct(d.cpuAvg);
      const bh = Math.max(1.5, (v / 100) * (base - 3));
      const x = i * cell + (cell - barW) / 2;

      const grad = g.createLinearGradient(0, base - bh, 0, base);
      if (today) {
        grad.addColorStop(0, 'rgba(215, 248, 255, 0.95)');
        grad.addColorStop(1, 'rgba(110, 200, 240, 0.45)');
      } else {
        grad.addColorStop(0, 'rgba(143, 230, 255, 0.55)');
        grad.addColorStop(1, 'rgba(143, 230, 255, 0.16)');
      }
      g.fillStyle = grad;
      g.fillRect(x, base - bh, barW, bh);

      g.fillStyle = today ? 'rgba(143, 230, 255, 0.95)' : 'rgba(143, 230, 255, 0.35)';
      g.fillRect(x, base - bh, barW, 1);

      g.fillStyle = today ? '#8ca0ae' : '#3b444d';
      g.fillText(label, i * cell + cell / 2, h - 1);
    }

    g.textAlign = 'left';
  }

  /* ---------------- top bar ---------------- */

  function kpi(key, value, pct, sub) {
    $('kv-' + key).textContent = value;
    $('kb-' + key).style.width = clampPct(pct) + '%';
    $('ks-' + key).textContent = sub;
  }

  /* ---------------- paint ---------------- */

  function paint(s) {
    const cpu = s.cpu || {};
    const mem = s.mem || {};
    const gpu = s.gpu || {};
    const net = s.net || {};
    const io = s.io || {};

    $('host').textContent = 'HALOFLUX';

    kpi('cpu', (cpu.total || 0).toFixed(0), cpu.total,
      `${cpu.coreCount || 16} CORES / ${cpu.mhz || 0} MHZ`);
    kpi('gpu', (gpu.util || 0).toFixed(0), gpu.util,
      gpu.present ? `${Math.round(gpu.tempC)}C / ${(gpu.memUsedMb / 1024).toFixed(1)}G` : 'NO GPU');
    kpi('mem', (mem.pct || 0).toFixed(0), mem.pct,
      `${(mem.usedMb / 1024).toFixed(1)} / ${(mem.totalMb / 1024).toFixed(0)} GB`);
    kpi('net', (net.downBps / 1048576).toFixed(1), (net.downBps / 1048576) / 20 * 100,
      `DN ${fmtShort(net.downBps)} UP ${fmtShort(net.upBps)}`);

    const sys = (s.disks || []).find((d) => d.id === 'C:') || (s.disks || [])[0];
    kpi('disk', sys ? sys.usedPct.toFixed(0) : '0', sys ? sys.usedPct : 0,
      sys ? `${sys.id} ${sys.freeGb.toFixed(0)}G FREE` : DASH);

    /* clock */
    const now = new Date();
    $('clock').textContent = now.toTimeString().slice(0, 5);
    $('dateline').textContent = `${DAY_NAMES[now.getDay()]} / ${now.toISOString().slice(0, 10)}`;
    $('uptime').textContent = 'UP ' + fmtDur(s.uptimeSec || 0);
    $('today').textContent = 'TODAY ' + fmtDur((s.todayMinutes || 0) * 60);

    /* processes */
    renderRows($('b-proc'), (s.top || []).slice(0, 6).map((p) => ({
      name: p.name.length > 17 ? p.name.slice(0, 16) + '..' : p.name,
      pct: Math.min(100, p.cpu * 2.2),
      val: p.cpu.toFixed(0) + '%',
      dim: p.cpu < 0.5,
    })));

    /* memory */
    const memPct = clampPct(mem.pct);
    const commitPct = mem.commitLimitMb ? clampPct(mem.commitMb / mem.commitLimitMb * 100) : 0;
    const topMem = (s.top || []).slice().sort((a, b) => b.memMb - a.memMb)[0];
    $('b-mem').innerHTML =
      `<div class="kvline"><span>PHYSICAL</span><span>${(mem.usedMb / 1024).toFixed(1)} / ${(mem.totalMb / 1024).toFixed(0)} GB</span></div>
       <div class="bigbar ${memPct > 88 ? 'hot' : memPct > 72 ? 'warn' : ''}"><i style="width:${memPct}%"></i></div>
       <div class="kvline"><span>COMMIT</span><span>${(mem.commitMb / 1024).toFixed(1)} / ${(mem.commitLimitMb / 1024).toFixed(0)} GB</span></div>
       <div class="bigbar"><i style="width:${commitPct}%"></i></div>
       <div class="kvline"><span>CACHE</span><span>${(mem.cachedMb / 1024).toFixed(1)} GB</span></div>
       <div class="kvline"><span>HEAVIEST</span><span>${topMem ? topMem.name.toUpperCase().slice(0, 12) + ' ' + (topMem.memMb / 1024).toFixed(1) + 'G' : DASH}</span></div>`;

    /* gpu */
    const vramPct = gpu.memTotalMb ? clampPct(gpu.memUsedMb / gpu.memTotalMb * 100) : 0;
    $('gpu-name').textContent = gpu.present ? 'NVML' : DASH;
    $('b-gpu').innerHTML = gpu.present
      ? `<div class="kvline"><span>UTIL</span><span class="c-gpu">${gpu.util.toFixed(0)} %</span></div>
         <div class="bigbar"><i style="width:${clampPct(gpu.util)}%"></i></div>
         <div class="kvline"><span>VRAM</span><span>${(gpu.memUsedMb / 1024).toFixed(1)} / ${(gpu.memTotalMb / 1024).toFixed(0)} GB</span></div>
         <div class="bigbar ${vramPct > 90 ? 'hot' : ''}"><i style="width:${vramPct}%"></i></div>
         <div class="kvline"><span>TEMP / PWR</span><span>${gpu.tempC.toFixed(0)}C / ${gpu.powerW.toFixed(0)} W</span></div>
         <div class="kvline"><span>CLOCK</span><span>${gpu.clockMhz.toFixed(0)} MHz</span></div>`
      : '<div class="kvline"><span>NO NVIDIA GPU</span><span>' + DASH + '</span></div>';

    /* network */
    state.histNet.push(net.downBps || 0);
    if (state.histNet.length > 64) state.histNet.shift();
    $('b-net').innerHTML =
      `<div class="kvline"><span>DOWN</span><span class="c-net">${fmtRate(net.downBps)}/s</span></div>
       <div class="kvline"><span>UP</span><span class="c-up">${fmtRate(net.upBps)}/s</span></div>
       <div class="kvline"><span>ADAPTER</span><span>${(net.iface || 'OFFLINE').toUpperCase()}</span></div>
       <canvas id="spark-net" style="width:100%;height:36px;margin-top:2px"></canvas>`;
    const sc = $('spark-net');
    if (sc) spark(sc, state.histNet, Math.max(131072, ...state.histNet), 'rgba(118,216,255,0.85)');

    /* volumes */
    renderRows($('b-vol'), (s.disks || []).map((d) => ({
      name: d.id,
      pct: d.usedPct,
      val: `${d.freeGb.toFixed(0)}G`,
      dim: d.id !== 'C:',
    })));

    /* cores */
    const cores = cpu.cores || [];
    const host = $('b-cores');
    if (!host.querySelector('.cores')) {
      host.innerHTML = '<div class="cores"></div>' +
        '<div class="kvline" style="margin-top:3px"><span>PEAK</span><span id="core-peak">--</span></div>' +
        '<div class="kvline"><span>LOAD 7D</span><span id="week-avg">--</span></div>' +
        '<canvas id="chart-week" style="width:100%;height:24px;margin-top:1px"></canvas>';
    }
    const grid = host.querySelector('.cores');
    while (grid.children.length < cores.length) {
      const c = document.createElement('div');
      c.className = 'core';
      c.innerHTML = '<i></i><span></span>';
      grid.appendChild(c);
    }
    for (let i = 0; i < grid.children.length; i++) {
      const c = grid.children[i];
      if (i >= cores.length) { c.style.display = 'none'; continue; }
      c.style.display = '';
      c.querySelector('i').style.height = clampPct(cores[i]) + '%';
      c.querySelector('span').textContent = i;
    }
    const peak = cores.length ? Math.max(...cores) : 0;
    const avg = cores.length ? cores.reduce((a, b) => a + b, 0) / cores.length : 0;
    const pk = $('core-peak');
    if (pk) pk.textContent = `${peak.toFixed(0)}% MAX / ${avg.toFixed(0)}% AVG`;
    $('core-count').textContent = `${cores.length} LOGICAL`;

    if (window.HFX && HFX.setStatus) {
      HFX.setStatus([
        { label: 'CPU', value: (cpu.total || 0).toFixed(0) + '%', pct: cpu.total || 0 },
        { label: 'GPU', value: (gpu.util || 0).toFixed(0) + '%', pct: gpu.util || 0 },
        { label: 'MEM', value: (mem.pct || 0).toFixed(0) + '%', pct: mem.pct || 0 },
        { label: 'NET', value: (net.downBps / 1048576).toFixed(1) + 'M', pct: Math.min(100, (net.downBps / 1048576) * 8) },
        { label: 'DISK', value: (sys ? sys.usedPct.toFixed(0) : '0') + '%', pct: sys ? sys.usedPct : 0 },
        { label: 'PROC', value: String(cpu.processes || 0), pct: Math.min(100, (cpu.processes || 0) / 4) },
        { label: 'I/O', value: ((io.readBps + io.writeBps) / 1048576).toFixed(1) + 'M', pct: Math.min(100, (io.readBps + io.writeBps) / 1048576 * 12) },
        { label: 'TEMP', value: (gpu.tempC ? gpu.tempC.toFixed(0) + 'C' : '--'), pct: (gpu.tempC || 0) },
      ]);
    }

    if (window.HFX && HFX.setPanels) {
      const procs = (s.top || []).slice(0, 4).map((p) => ({
        label: String(p.name || '').slice(0, 12),
        value: (p.cpu || 0).toFixed(0) + '%',
        pct: p.cpu || 0,
      }));
      const coreVals = (cpu.cores || []).slice(0, 16);

      HFX.setPanels([
        { title: 'PROCESS FEED', tag: 'TOP CPU', rows: procs },
        {
          title: 'MEMORY', tag: 'COMMIT', rows: [
            { label: 'PHYSICAL', value: ((mem.usedGb || 0).toFixed(1)) + ' / ' + ((mem.totalGb || 0).toFixed(0)) + ' GB', pct: mem.pct || 0 },
            { label: 'CACHE', value: ((mem.cacheGb || 0).toFixed(1)) + ' GB', pct: Math.min(100, (mem.cacheGb || 0) * 10) },
            { label: 'HEAVIEST', value: String((mem.heaviest || '').slice(0, 10)), pct: 0 },
          ]
        },
        {
          title: 'GPU', tag: 'NVIDIA', rows: [
            { label: 'UTIL', value: (gpu.util || 0).toFixed(0) + '%', pct: gpu.util || 0 },
            { label: 'VRAM', value: (gpu.vramUsed || 0).toFixed(1) + ' / ' + (gpu.vramTotal || 0).toFixed(0) + ' GB', pct: gpu.vramTotal ? (gpu.vramUsed / gpu.vramTotal) * 100 : 0 },
            { label: 'TEMP', value: (gpu.tempC ? gpu.tempC.toFixed(0) : '--') + 'C', pct: gpu.tempC || 0 },
          ]
        },
        {
          title: 'NETWORK', tag: 'ADAPTER', rows: [
            { label: 'DOWN', value: (net.downBps / 1024).toFixed(0) + ' KB/s', pct: Math.min(100, net.downBps / 1048576 * 10) },
            { label: 'UP', value: (net.upBps / 1024).toFixed(0) + ' KB/s', pct: Math.min(100, net.upBps / 1048576 * 10) },
            { label: 'WLAN', value: 'online', pct: 0 },
          ]
        },
        {
          title: 'VOLUMES', tag: 'STORAGE', rows: [
            { label: 'C:', value: 'free ' + (sys ? sys.freeGb.toFixed(0) : '0') + 'G', pct: sys ? sys.usedPct : 0 },
            { label: 'D:', value: 'ok', pct: 0 },
            { label: 'E:', value: 'ok', pct: 0 },
          ]
        },
        {
          title: 'CORE MATRIX', tag: '16 LOGICAL', rows: [
            { label: 'PEAK', value: Math.max(0, Math.max.apply(null, coreVals.concat([0]))).toFixed(0) + '%', pct: Math.max.apply(null, coreVals.concat([0])) },
            { label: 'AVG', value: (cpu.total || 0).toFixed(0) + '%', pct: cpu.total || 0 },
            { label: 'LOAD', value: '1M', pct: 0 },
          ]
        },
        {
          title: 'I/O', tag: 'DISK', rows: [
            { label: 'READ', value: (io.readBps / 1024).toFixed(0) + ' KB/s', pct: Math.min(100, io.readBps / 1048576 * 20) },
            { label: 'WRITE', value: (io.writeBps / 1024).toFixed(0) + ' KB/s', pct: Math.min(100, io.writeBps / 1048576 * 20) },
            { label: 'BATTERY', value: (((s.battery||{}).pct) || 100).toFixed(0) + '%', pct: ((s.battery||{}).pct) || 0 },
          ]
        },
        {
          title: 'SYSTEM', tag: 'STATUS', rows: [
            { label: 'HOST', value: 'HALOFLUX', pct: 0 },
            { label: 'PROC', value: String(cpu.processes || 0), pct: Math.min(100, (cpu.processes || 0) / 4) },
            { label: 'CPU', value: String((cpu.name || '').slice(0, 12)), pct: cpu.total || 0 },
          ]
        },
      ]);
    }

    const hist = s.history || [];
    const withData = hist.filter((d) => (d.n || 0) > 0);
    const weekAvg = withData.length
      ? withData.reduce((a, d) => a + (d.cpuAvg || 0), 0) / withData.length
      : 0;
    const wa = $('week-avg');
    if (wa) wa.textContent = withData.length ? `${weekAvg.toFixed(0)}% / ${withData.length}D` : 'NO DATA YET';
    const wc = $('chart-week');
    if (wc) weekChart(wc, hist);

    /* io + battery */
    state.histIo.push((io.readBps || 0) + (io.writeBps || 0));
    if (state.histIo.length > 64) state.histIo.shift();
    const bat = s.battery || {};
    let batTxt = 'AC / DESKTOP';
    if (bat.present) {
      if (bat.charging) batTxt = `${bat.pct.toFixed(0)}% / CHARGING`;
      else if (bat.minutesLeft > 0) batTxt = `${bat.pct.toFixed(0)}% / ${Math.floor(bat.minutesLeft / 60)}h ${bat.minutesLeft % 60}m LEFT`;
      else batTxt = `${bat.pct.toFixed(0)}% / ON BATTERY`;
    }
    $('b-io').innerHTML =
      `<div class="kvline"><span>READ</span><span class="c-rd">${fmtRate(io.readBps)}/s</span></div>
       <div class="kvline"><span>WRITE</span><span class="c-wr">${fmtRate(io.writeBps)}/s</span></div>
       <canvas id="spark-io" style="width:100%;height:30px;margin-top:1px"></canvas>
       <div class="kvline"><span>BATTERY</span><span>${batTxt}</span></div>`;
    const si = $('spark-io');
    if (si) spark(si, state.histIo, Math.max(524288, ...state.histIo), 'rgba(200,170,255,0.8)');

    /* system + 7 day load */
    $('b-sys').innerHTML =
      `<div class="stat-grid">
         <div class="kvline"><span>HOST</span><span>HALOFLUX</span></div>
         <div class="kvline"><span>USER</span><span>${(s.user || DASH).toUpperCase()}</span></div>
         <div class="kvline"><span>PROC / THR</span><span>${cpu.processes || 0} / ${cpu.threads || 0}</span></div>
         <div class="kvline"><span>HANDLES</span><span>${cpu.handles || 0}</span></div>
         <div class="kvline"><span>CPU</span><span>${(cpu.name || DASH).replace(/AMD |with Radeon.*$/g, '').trim().slice(0, 15)}</span></div>
         <div class="kvline"><span>GPU</span><span>${(gpu.name || DASH).replace('NVIDIA GeForce ', '').slice(0, 13)}</span></div>
       </div>`;
  }

  /* ---------------- feed ---------------- */

  async function tick() {
    try {
      const res = await fetch('/api/stats', { cache: 'no-store' });
      if (!res.ok) throw new Error('http ' + res.status);
      const s = await res.json();
      state.last = s;
      state.lastOk = performance.now();
      $('mode').classList.remove('off');
      HFX.feed(s);
      paint(s);
    } catch (err) {
      if (performance.now() - state.lastOk > 3200) $('mode').classList.add('off');
    }
  }

  /* ---------------- boot ---------------- */

  async function audioTick() {
    try {
      const res = await fetch('/api/audio', { cache: 'no-store' });
      if (!res.ok) return;
      const f = await res.json();
      HFX.feedAudio(f);
      const au = $('au');
      if (au) {
        if (f.live) {
          const pct = Math.round((f.level || 0) * 100);
          const bars = '|'.repeat(1 + Math.min(9, Math.round((f.level || 0) * 9)));
          au.textContent = `AUDIO ${pct}% ${bars}`;
          au.style.color = '#8fe6ff';
        } else {
          au.textContent = 'AUDIO IDLE';
          au.style.color = '#4a545e';
        }
      }
    } catch { }
  }

  async function windowsTick() {
    try {
      const res = await fetch('/api/windows', { cache: 'no-store' });
      if (res.ok) HFX.feedWindows(await res.json());
    } catch { }
  }

  async function iconsTick() {
    try {
      const res = await fetch('/api/icons', { cache: 'no-store' });
      if (res.ok) HFX.feedIcons(await res.json());
    } catch { }
  }

  const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const bgm = document.getElementById('bgm');

  function syncTransport() {
    const on = !!bgm && !bgm.paused;
    if (window.HFX) HFX.setAudio(on);
  }

  function toggleAudio() {
    if (!bgm) return;
    if (bgm.paused) {
      bgm.volume = 0.55;
      bgm.play().then(syncTransport).catch(() => { });
    } else {
      bgm.pause();
      syncTransport();
    }
  }

  function publishHits() {
    if (!host || !window.HFX) return;
    try {
      window.__hitsSent = (window.__hitsSent | 0) + 1;
      host.postMessage({ type: 'hits', rects: HFX.hits() });
    } catch { }
  }

  function onHostMessage(e) {
    try {
      const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
      if (!msg) return;
      if (msg.type === 'cursor' && window.HFX) HFX.setCursor(msg.x, msg.y, msg.over);
      if (msg.type === 'music' && window.HFX) HFX.setMusic(msg.pos, msg.dur, msg.playing, msg.name);
    } catch { }
  }

  function onClick(e) {
    if (!window.HFX) return;
    const rects = HFX.hits();
    for (const b of rects) {
      if (e.clientX >= b.x && e.clientX <= b.x + b.w && e.clientY >= b.y && e.clientY <= b.y + b.h) {
        if (b.action === 'audio') {
          toggleAudio();
        } else {
          const cx0 = window.innerWidth * 0.635;
          const cy0 = window.innerHeight * 0.445;
          const bx = b.x + b.w / 2 - cx0;
          const by = b.y + b.h / 2 - cy0;
          if (HFX.flashAt) HFX.flashAt(Math.atan2(by, bx));
          if (HFX.toggleDetail) HFX.toggleDetail();
        }
        return;
      }
    }
  }

  function layoutPanels() {
    const w = window.innerWidth;
    const h = window.innerHeight;
    const cx = w * 0.635;
    const cy = h * 0.445;

    const ax = w * 0.375;
    const ay = h * 0.300;
    const tilt = -0.34;

    const panels = document.querySelectorAll('#hud .panel');
    const n = panels.length;
    const base0 = (performance.now() / 1000) * 0.052;

    for (let i = 0; i < n; i++) {
      const a = base0 + (i / n) * Math.PI * 2 - Math.PI * 0.5;

      const ex = Math.cos(a) * ax;
      const ey = Math.sin(a) * ay;
      const x = cx + ex * Math.cos(tilt) - ey * Math.sin(tilt);
      const y = cy + ex * Math.sin(tilt) + ey * Math.cos(tilt);

      const tx = -ax * Math.sin(a);
      const ty = ay * Math.cos(a);
      let rotRad = Math.atan2(ty, tx) + tilt;
      if (Math.cos(rotRad) < 0) rotRad += Math.PI;

      let deg = (rotRad * 180 / Math.PI) * 0.72;
      while (deg > 180) deg -= 360;
      while (deg < -180) deg += 360;

      // only a hint of lean: enough to feel the curve, never so much that it shouts
      if (deg > 12) deg = 12 + (deg - 12) * 0.10;
      if (deg < -12) deg = -12 + (deg + 12) * 0.10;

      const depth = (Math.sin(a) + 1) / 2;
      const sx = 0.56 + depth * 0.12;
      const sy = sx * (0.86 + depth * 0.14);

      const el = panels[i];
      el.style.left = x.toFixed(1) + 'px';
      el.style.top = y.toFixed(1) + 'px';
      el.style.transform = `translate(-50%, -50%) rotate(${deg.toFixed(2)}deg) scale(${sx.toFixed(3)}, ${sy.toFixed(3)})`;

      const near = Math.min(x - 60, w - 60 - x, y - 60, h - 40 - y);
      const vis = near <= 0 ? 0 : Math.min(1, near / 90);
      el.style.opacity = (vis * (0.26 + depth * 0.34)).toFixed(2);
    }

    requestAnimationFrame(layoutPanels);
  }

  function boot() {
    tick();
    setInterval(tick, 1000);

    layoutPanels();

    if (host) {
      host.addEventListener('message', onHostMessage);
      window.addEventListener('click', onClick);
      setInterval(publishHits, 120);
    }
    if (bgm) {
      if (location.search.indexOf('autoplay=1') >= 0) {
        setTimeout(() => { try { bgm.volume = 0.55; bgm.play().catch(() => { }); syncTransport(); } catch (e) { } }, 600);
      }
      bgm.addEventListener('play', syncTransport);
      bgm.addEventListener('pause', syncTransport);
      syncTransport();
    }
    windowsTick();
    setInterval(windowsTick, 3000);
    iconsTick();
    setInterval(iconsTick, 60000);
    audioTick();
    setInterval(audioTick, 50);

    let hud = true;
    window.addEventListener('keydown', (e) => {
      if (e.key === 'h' || e.key === 'H') {
        hud = !hud;
        $('hud').classList.toggle('hidden', !hud);
      }
      if (e.key === 'c' || e.key === 'C') {
        const ui = $('ui');
        ui.style.opacity = ui.style.opacity === '0' ? '1' : '0';
      }
    });
  }

  if (document.readyState === 'loading') {
    window.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();

