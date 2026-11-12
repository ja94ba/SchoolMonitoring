/* ==========================================================================
   app.js  -  SafeClass Monitor (front-end prototype)
   --------------------------------------------------------------------------
   How this file is organised (top to bottom):
     1. Setup & small helpers
     2. State (the app's current data)
     3. HTML "templates" (functions that return markup)
     4. Render functions (put markup on the page)
     5. Actions (acknowledge, resolve, simulate ...)
     6. Navigation
     7. Events & start-up

   To connect real devices later, you mainly change step 5: instead of editing
   `state` directly, call your cloud database / ESP32 messages, then call
   renderAll() again.
   ========================================================================== */
(function () {
  'use strict';

  /* ---------- 1. Setup & helpers ---------- */
  const $  = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const icon = (name, cls = '') => `<svg class="i ${cls}" aria-hidden="true"><use href="#i-${name}"/></svg>`;

  const VIEWS = {
    dashboard:  { hash: 'dashboard',    title: 'Dashboard',        sub: 'Overview of classroom conditions' },
    classrooms: { hash: 'classrooms',   title: 'Classrooms',       sub: 'All registered classrooms and their devices' },
    alerts:     { hash: 'alerts',       title: 'Emergency Alerts', sub: 'Acknowledge and resolve incoming alerts' },
    settings:   { hash: 'app-settings', title: 'Settings',         sub: 'School, account and notification options' }
  };

  const STATUS = {
    normal:    { label: 'Normal',    icon: 'check' },
    emergency: { label: 'Emergency', icon: 'warn' },
    offline:   { label: 'Offline',   icon: 'off' }
  };
  const ALERT_STATUS = {
    active:       { label: 'Active',       icon: 'warn' },
    acknowledged: { label: 'Acknowledged', icon: 'eye' },
    resolved:     { label: 'Resolved',     icon: 'check' }
  };
  const SIGNAL_LABEL = ['No signal', 'Weak', 'Fair', 'Good', 'Strong'];

  const pad = (n) => n;
  const fmtTime = (ts) => new Date(ts).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' });
  const fmtDateTime = (ts) => new Date(ts).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }) + ' · ' + fmtTime(ts);

  // "12 s ago", "4 min ago", "3 h ago", "2 days ago"
  function rel(ts) {
    const s = Math.max(0, Math.floor((Date.now() - ts) / 1000));
    if (s < 5) return 'Just now';
    if (s < 60) return s + ' s ago';
    const m = Math.floor(s / 60);
    if (m < 60) return m + ' min ago';
    const h = Math.floor(m / 60);
    if (h < 24) return h + ' h ago';
    const d = Math.floor(h / 24);
    return d + (d === 1 ? ' day ago' : ' days ago');
  }
  const relSpan = (ts, extra = '') => `<span data-rel data-ts="${ts}" ${extra}>${rel(ts)}</span>`;
  const pick = (arr) => arr[Math.floor(Math.random() * arr.length)];

  /* ---------- 2. State ---------- */
  const state = Object.assign(
    { view: 'dashboard', filter: 'all', query: '', school: { name: SAMPLE_SCHOOL.name }, openRoom: null },
    buildSampleData(Date.now())
  );

  const roomById = (id) => state.classrooms.find((r) => r.id === id);
  const openAlerts = () => state.alerts.filter((a) => a.status !== 'resolved');
  const newAlerts = () => state.alerts.filter((a) => a.status === 'active');

  function addActivity(text, kind) {
    state.activity.unshift({ ts: Date.now(), kind, text });
    state.activity.length = Math.min(state.activity.length, 30);
  }

  /* ---------- 3. Templates ---------- */
  const pill = (map, key) => `<span class="pill pill--${key}">${icon(map[key].icon)}${map[key].label}</span>`;

  function connHTML(r) {
    const online = r.status !== 'offline';
    return `<span class="conn ${online ? '' : 'conn--off'}"><span class="dot"></span>${online ? 'Online' : 'Offline'}` +
           `<span class="signal" data-level="${online ? r.signal : 0}" title="${SIGNAL_LABEL[online ? r.signal : 0]}"><i></i><i></i><i></i><i></i></span></span>`;
  }

  function roomRow(r) {
    return `<button class="room-row room-row--${r.status}" data-action="open-room" data-room="${r.id}">
      <span class="cell cell--name"><span class="room-code">${esc(r.code)}</span>
        <span class="room-name"><b>${esc(r.name)}</b><small>${esc(r.building)} · ${esc(r.floor)}</small></span></span>
      <span class="cell cell--status">${pill(STATUS, r.status)}</span>
      <span class="cell cell--conn">${connHTML(r)}</span>
      <span class="cell cell--time">${icon('clock')}${relSpan(r.lastUpdate, `data-room-ts="${r.id}"`)}</span>
      ${icon('chev', 'chev')}
    </button>`;
  }

  function alertActions(a) {
    if (a.status === 'active') {
      return `<button class="btn btn--danger" data-action="ack" data-alert="${a.id}">${icon('eye')}Acknowledge</button>
              <button class="btn btn--outline" data-action="resolve" data-alert="${a.id}">${icon('check')}Mark resolved</button>`;
    }
    if (a.status === 'acknowledged') {
      return `<button class="btn btn--primary" data-action="resolve" data-alert="${a.id}">${icon('check')}Mark resolved</button>`;
    }
    return '';
  }

  function alertCard(a, compact) {
    const r = roomById(a.roomId);
    let handled = '';
    if (a.status === 'acknowledged') handled = `Acknowledged by ${esc(SAMPLE_USER.name)} at ${fmtTime(a.ackAt)}`;
    if (a.status === 'resolved') handled = `Resolved by ${esc(SAMPLE_USER.name)} at ${fmtTime(a.resolvedAt)}`;
    const iconName = a.status === 'resolved' ? 'check' : a.status === 'acknowledged' ? 'eye' : 'warn';
    return `<article class="alert alert--${a.status} ${compact ? 'alert--compact' : ''}">
      <div class="alert-main">
        <span class="alert-icon">${icon(iconName)}</span>
        <div class="alert-body">
          <div class="alert-top"><h3>${esc(a.type)}</h3>${pill(ALERT_STATUS, a.status)}</div>
          <div class="alert-meta"><button class="link" data-action="open-room" data-room="${r.id}">${esc(r.code)} · ${esc(r.name)}</button></div>
          <div class="alert-meta">${icon('clock')}<span>${fmtDateTime(a.receivedAt)}</span><span>(${relSpan(a.receivedAt)})</span></div>
          ${handled ? `<div class="alert-meta">${handled}</div>` : ''}
        </div>
      </div>
      <div class="alert-actions">${alertActions(a)}</div>
    </article>`;
  }

  const emptyState = (title, text, plain) =>
    `<div class="empty ${plain ? 'empty--plain' : ''}">${icon(plain ? 'search' : 'shield')}<b>${title}</b><span>${text}</span></div>`;

  /* ---------- 4. Render functions ---------- */
  function renderBrand() {
    $$('.js-school').forEach((el) => (el.textContent = state.school.name));
    $('#schoolName').value = state.school.name;
    document.title = 'SafeClass Monitor';
  }

  function renderSummary() {
    const count = (s) => state.classrooms.filter((r) => r.status === s).length;
    const open = openAlerts().length, needAck = newAlerts().length;
    const buildings = new Set(state.classrooms.map((r) => r.building)).size;
    const stat = (cls, label, num, note, ic, extra = '') =>
      `<div class="stat stat--${cls} ${extra}"><span class="stat-label">${label}</span><span class="stat-icon">${icon(ic)}</span><strong class="stat-num">${num}</strong><span class="stat-note">${note}</span></div>`;
    $('#summary').innerHTML =
      stat('total', 'Total classrooms', state.classrooms.length, `Across ${buildings} buildings`, 'room') +
      stat('normal', 'Normal status', count('normal'), 'Reporting normal conditions', 'check') +
      stat('emergency', 'Emergency alerts', open, open ? (needAck ? `${needAck} awaiting acknowledgment` : 'All acknowledged') : 'No active alerts', 'warn', open ? 'is-hot' : '') +
      stat('offline', 'Offline devices', count('offline'), 'Not reporting', 'off');
  }

  function renderBanner() {
    const list = newAlerts().sort((a, b) => b.receivedAt - a.receivedAt);
    const el = $('#banner');
    if (!list.length) { el.innerHTML = ''; return; }
    const a = list[0], r = roomById(a.roomId);
    const title = list.length > 1 ? `${list.length} emergency alerts need acknowledgment` : `Emergency alert: ${esc(r.name)}`;
    const detail = `${esc(r.code)} · ${esc(a.type)} · ${relSpan(a.receivedAt)}`;
    el.innerHTML = `<div class="banner" role="alert"><span class="pulse"></span>
      <div class="banner-text"><strong>${title}</strong><span>${list.length > 1 ? 'Latest: ' : ''}${detail}</span></div>
      <button class="btn btn--light" data-action="go" data-view="alerts">Respond</button></div>`;
  }

  function renderNavBadges() {
    const n = openAlerts().length;
    $$('[data-alert-badge]').forEach((el) => { el.textContent = n; el.hidden = n === 0; });
  }

  const severity = { emergency: 0, normal: 1, offline: 2 };

  function renderDashboard() {
    renderSummary();
    const alerts = openAlerts().sort((a, b) => (a.status === 'active' ? 0 : 1) - (b.status === 'active' ? 0 : 1) || b.receivedAt - a.receivedAt);
    $('#dashAlerts').innerHTML = alerts.length
      ? alerts.map((a) => alertCard(a, true)).join('')
      : emptyState('All clear', 'No active alerts. Every online classroom is reporting normally.');
    const rooms = state.classrooms.slice().sort((a, b) => severity[a.status] - severity[b.status] || a.code.localeCompare(b.code));
    $('#dashRooms').innerHTML = rooms.slice(0, 8).map(roomRow).join('');
    renderFeed();
  }

  function renderFeed() {
    $('#feed').innerHTML = state.activity.slice(0, 6).map((e) =>
      `<li class="feed-item feed-item--${e.kind}"><span class="feed-dot"></span><span>${esc(e.text)}</span>${relSpan(e.ts).replace('<span', '<span class="feed-time"')}</li>`
    ).join('');
  }

  function renderClassrooms() {
    const q = state.query.trim().toLowerCase();
    const matchQuery = (r) => !q || [r.code, r.name, r.building, r.floor, r.adviser, r.device, STATUS[r.status].label].join(' ').toLowerCase().includes(q);
    const base = state.classrooms.filter(matchQuery);
    const shown = base.filter((r) => state.filter === 'all' || r.status === state.filter);

    ['all', 'normal', 'emergency', 'offline'].forEach((k) => {
      const n = k === 'all' ? base.length : base.filter((r) => r.status === k).length;
      $(`[data-count="${k}"]`).textContent = n;
    });
    $$('.chip').forEach((c) => c.setAttribute('aria-pressed', String(c.dataset.filter === state.filter)));

    $('#roomList').innerHTML = shown.length
      ? shown.map(roomRow).join('')
      : emptyState('No classrooms found', 'Try a different search or choose another status filter.', true);
    $('#resultNote').textContent = `Showing ${shown.length} of ${state.classrooms.length} classrooms. Select a row to see device details.`;
  }

  function renderAlertsView() {
    const active = openAlerts().sort((a, b) => (a.status === 'active' ? 0 : 1) - (b.status === 'active' ? 0 : 1) || b.receivedAt - a.receivedAt);
    const resolved = state.alerts.filter((a) => a.status === 'resolved').sort((a, b) => b.receivedAt - a.receivedAt);
    $('#activeCount').textContent = active.length;
    $('#activeCount').hidden = active.length === 0;
    $('#resolvedCount').textContent = resolved.length;
    $('#alertsActive').innerHTML = active.length
      ? active.map((a) => alertCard(a, false)).join('')
      : `<div class="panel">${emptyState('All clear', 'No active alerts. Every online classroom is reporting normally.')}</div>`;
    $('#alertsResolved').innerHTML = resolved.length
      ? resolved.map((a) => alertCard(a, false)).join('')
      : `<div class="panel">${emptyState('Nothing resolved yet', 'Resolved alerts will be listed here.', true)}</div>`;
  }

  function renderSettingsRooms() {
    $('#regCount').textContent = state.classrooms.length;
    $('#regList').innerHTML = state.classrooms.map((r) =>
      `<div class="reg-row"><span class="room-code">${esc(r.code)}</span><span><b>${esc(r.name)}</b></span><span class="mono">${esc(r.device)}</span><span class="loc muted">${esc(r.building)} · ${esc(r.floor)}</span></div>`
    ).join('');
  }

  function renderDialog() {
    const dlg = $('#roomDialog');
    const r = state.openRoom && roomById(state.openRoom);
    if (!r) return;
    const history = state.alerts.filter((a) => a.roomId === r.id).sort((a, b) => b.receivedAt - a.receivedAt);
    const pending = history.filter((a) => a.status !== 'resolved');
    $('#dlgBody').innerHTML = `<div class="dlg">
      <div class="dlg-head">
        <div><span class="room-code">${esc(r.code)}</span><h2 id="dlgTitle">${esc(r.name)}</h2><p>${esc(r.building)} · ${esc(r.floor)}</p></div>
        <button class="icon-btn" data-action="close-dialog" aria-label="Close details">${icon('close')}</button>
      </div>
      <div>${pill(STATUS, r.status)}</div>
      <dl class="detail">
        <div><dt>Adviser</dt><dd>${esc(r.adviser)}</dd></div>
        <div><dt>Device ID</dt><dd>${esc(r.device)}</dd></div>
        <div><dt>Connection</dt><dd>${connHTML(r)}</dd></div>
        <div><dt>Signal</dt><dd>${SIGNAL_LABEL[r.status === 'offline' ? 0 : r.signal]}</dd></div>
        <div><dt>Last status update</dt><dd>${relSpan(r.lastUpdate, `data-room-ts="${r.id}"`)}</dd></div>
        <div><dt>Reported at</dt><dd>${fmtDateTime(r.lastUpdate)}</dd></div>
      </dl>
      <div>
        <h3>Alert history</h3>
        <ul class="history">${history.length ? history.map((a) =>
          `<li><span>${esc(a.type)}<small>${fmtDateTime(a.receivedAt)}</small></span>${pill(ALERT_STATUS, a.status)}</li>`).join('') : '<li class="muted">No alerts recorded for this classroom.</li>'}</ul>
      </div>
      ${pending.length ? `<div class="alert-actions" style="justify-content:flex-start">${alertActions(pending[0])}</div>` : ''}
    </div>`;
    if (!dlg.open) { try { dlg.showModal(); } catch (e) { dlg.setAttribute('open', ''); } }
  }

  function renderAll() {
    renderBrand();
    renderNavBadges();
    renderBanner();
    renderDashboard();
    renderClassrooms();
    renderAlertsView();
    renderSettingsRooms();
    if (state.openRoom && $('#roomDialog').open) renderDialog();
  }

  function refreshTimes() {
    $$('[data-rel]').forEach((el) => { el.textContent = rel(Number(el.dataset.ts)); });
    $('#clock').textContent = new Date().toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' }) + ' · ' +
      new Date().toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', second: '2-digit' });
  }

  /* ---------- 5. Actions (demo only: they just change `state`) ---------- */
  function toast(msg) {
    const el = $('#toast');
    el.textContent = msg;
    el.classList.add('show');
    clearTimeout(toast.t);
    toast.t = setTimeout(() => el.classList.remove('show'), 3200);
  }

  function acknowledge(id) {
    const a = state.alerts.find((x) => x.id === id);
    if (!a || a.status !== 'active') return;
    a.status = 'acknowledged';
    a.ackAt = Date.now();
    const r = roomById(a.roomId);
    addActivity(`${r.code} ${a.type} alert acknowledged`, 'emergency');
    renderAll();
    toast(`Alert from ${r.code} acknowledged.`);
  }

  function resolve(id) {
    const a = state.alerts.find((x) => x.id === id);
    if (!a || a.status === 'resolved') return;
    const now = Date.now();
    if (a.status === 'active') a.ackAt = now;
    a.status = 'resolved';
    a.resolvedAt = now;
    const r = roomById(a.roomId);
    const stillOpen = openAlerts().some((x) => x.roomId === r.id);
    if (!stillOpen && r.status === 'emergency') { r.status = 'normal'; r.lastUpdate = now; }
    addActivity(`${r.code} ${r.name} alert resolved, status back to normal`, 'normal');
    renderAll();
    toast(`Alert from ${r.code} marked as resolved.`);
  }

  function simulateAlert() {
    const pool = state.classrooms.filter((r) => r.status === 'normal');
    if (!pool.length) { toast('No normal classrooms left to trigger. Resolve alerts or reset the demo.'); return; }
    const r = pick(pool), type = pick(ALERT_TYPES), now = Date.now();
    state.alerts.unshift({ id: 'al' + now, roomId: r.id, type, receivedAt: now, status: 'active' });
    r.status = 'emergency';
    r.lastUpdate = now;
    addActivity(`${r.code} ${r.name} sent a ${type} alert`, 'emergency');
    renderAll();
    toast(`New alert: ${type} from ${r.code}.`);
  }

  function resetDemo() {
    Object.assign(state, buildSampleData(Date.now()), { openRoom: null, filter: 'all', query: '' });
    $('#search').value = '';
    closeDialog();
    renderAll();
    toast('Demo data reset.');
  }

  // Pretend the ESP32 devices are checking in every few seconds.
  function heartbeat() {
    const online = state.classrooms.filter((r) => r.status !== 'offline');
    if (!online.length) return;
    const now = Date.now();
    const picked = online.slice().sort(() => Math.random() - 0.5).slice(0, 1 + Math.floor(Math.random() * 3));
    picked.forEach((r) => {
      r.lastUpdate = now;
      $$(`[data-room-ts="${r.id}"]`).forEach((el) => { el.dataset.ts = now; });
    });
    const r = picked[0];
    addActivity(`${r.code} ${r.name} reported ${r.status === 'emergency' ? 'an active emergency' : 'normal conditions'}`, r.status === 'emergency' ? 'emergency' : 'normal');
    renderFeed();
    refreshTimes();
  }

  function closeDialog() {
    const dlg = $('#roomDialog');
    state.openRoom = null;
    if (dlg.open) dlg.close();
  }

  /* ---------- 6. Navigation ---------- */
  function showView(v) {
    if (!VIEWS[v]) v = 'dashboard';
    state.view = v;
    document.body.dataset.view = v;
    $$('.view').forEach((s) => { s.hidden = s.dataset.view !== v; });
    $$('.nav-link, .tab').forEach((a) => {
      if (a.dataset.view === v) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });
    $('#pageTitle').textContent = VIEWS[v].title;
    $('#pageSub').textContent = VIEWS[v].sub;
    window.scrollTo(0, 0);
  }

  function go(v) {
    showView(v);
    try { if (location.hash !== '#' + VIEWS[v].hash) location.hash = VIEWS[v].hash; } catch (e) { /* hash not available: ignore */ }
  }

  const viewFromHash = () => Object.keys(VIEWS).find((k) => VIEWS[k].hash === location.hash.slice(1));

  /* ---------- 7. Events & start-up ---------- */
  document.addEventListener('click', (e) => {
    const t = e.target.closest('[data-action]');
    if (t) {
      const act = t.dataset.action;
      if (act === 'go') { e.preventDefault(); closeDialog(); go(t.dataset.view); }
      else if (act === 'open-room') { state.openRoom = t.dataset.room; renderDialog(); }
      else if (act === 'close-dialog') closeDialog();
      else if (act === 'ack') acknowledge(t.dataset.alert);
      else if (act === 'resolve') resolve(t.dataset.alert);
      else if (act === 'simulate') simulateAlert();
      else if (act === 'reset') resetDemo();
      else if (act === 'filter') { state.filter = t.dataset.filter; renderClassrooms(); }
      else if (act === 'save-school') {
        const v = $('#schoolName').value.trim();
        if (!v) { toast('Enter a school name first.'); return; }
        state.school.name = v;
        renderBrand();
        toast('School name updated (demo only, not saved).');
      }
      return;
    }
    // Click on the dark backdrop closes the popup
    if (e.target === $('#roomDialog')) closeDialog();
  });

  $('#roomDialog').addEventListener('close', () => { state.openRoom = null; });
  $('#search').addEventListener('input', (e) => { state.query = e.target.value; renderClassrooms(); });
  window.addEventListener('hashchange', () => { const v = viewFromHash(); if (v && v !== state.view) showView(v); });

  renderAll();
  showView(viewFromHash() || 'dashboard');
  refreshTimes();
  setInterval(refreshTimes, 1000);   // clock + "x s ago" labels
  setInterval(heartbeat, 7000);      // fake device check-ins
})();
