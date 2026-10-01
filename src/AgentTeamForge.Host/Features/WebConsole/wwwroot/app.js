'use strict';
// No HTML from agents: server text is rendered with textContent or the safe markdown renderer.
// A fragment token is copied to per-tab storage and removed from the address bar.
(() => {
  const $ = (id) => document.getElementById(id);
  // Jobs accepted before derived names carry the setup profile's placeholder agent.
  const agentName = (j) => j.target_agent && j.target_agent !== 'fake-agent' ? j.target_agent
    : (j.backend || 'agent') + '-' + j.job_id.slice(-8);
  const tokenKey = 'atf.web.token';
  const themeKey = 'atf.web.theme';
  const teamsKey = 'atf.web.teams';
  let savedTeams = {};
  try {
    const saved = JSON.parse(localStorage.getItem(teamsKey) || '{}');
    if (saved && typeof saved === 'object' && !Array.isArray(saved)) savedTeams = saved;
  } catch { /* Keep in-tab defaults. */ }
  const maxSavedTeams = 200;
  function saveTeams(key, value) {
    if (key) {
      delete savedTeams[key];
      savedTeams[key] = value;
    }
    const keys = Object.keys(savedTeams);
    for (const old of keys.slice(0, Math.max(0, keys.length - maxSavedTeams))) delete savedTeams[old];
    try { localStorage.setItem(teamsKey, JSON.stringify(savedTeams)); } catch { /* Keep the in-tab choice. */ }
  }
  // Master-detail layout; this query must match the min-width @media block in app.css.
  const wide = matchMedia('(min-width: 1100px)');
  // Pseudo job id for the composer target that messages the lead's inbox instead of a member job.
  const LEAD_TARGET = '@lead';
  let theme = 'auto';
  try {
    const saved = localStorage.getItem(themeKey);
    if (saved === 'light' || saved === 'dark') theme = saved;
  } catch { /* Storage may be blocked; Auto still works. */ }

  function applyTheme(value) {
    if (value === 'auto') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.dataset.theme = value;
  }
  applyTheme(theme);
  let token = null;
  let expandedKey = null;
  let pageCursors = [null];
  let pageIndex = 0;
  let nextCursor = null;
  const composers = new Map();
  const cardLogs = new Map();
  const activities = new Map();
  const jobDetails = new Map();
  const knownLeads = new Map();
  const leadNames = new Map();
  let leadOptionsKey = '';
  const tickets = new Map();
  const newAgent = { pending: null, sending: false };
  let modelOptions = {};
  let tierSettings = [];
  let herdrMode = false;
  let defaultPlacement = 'herdr-session:default';
  const recentCwds = new Map();
  let pickedDirectory = null;
  let timer = null;
  let ticketTimer = null;
  let listSeq = 0;

  function setStatus(text, cls) {
    const s = $('status');
    s.textContent = text;
    s.className = cls || '';
  }

  async function api(method, path, body) {
    const controller = new AbortController();
    const deadline = setTimeout(() => controller.abort(), 20000);
    try {
      const init = {
        method,
        headers: { 'Authorization': 'Bearer ' + token },
        cache: 'no-store',
        credentials: 'omit',
        referrerPolicy: 'no-referrer',
        signal: controller.signal,
      };
      if (body !== undefined) {
        init.headers['Content-Type'] = 'application/json';
        init.body = JSON.stringify(body);
      }
      const res = await fetch(path, init);
      if (res.status === 401) {
        logout('Token rejected.');
        return null;
      }
      return await res.json();
    } catch (e) {
      return { ok: false, error: 'network', lost: true };
    } finally {
      clearTimeout(deadline);
    }
  }

  function logout(message) {
    token = null;
    sessionStorage.removeItem(tokenKey);
    clearInterval(timer);
    clearInterval(ticketTimer);
    tickets.clear();
    knownLeads.clear();
    leadNames.clear();
    leadOptionsKey = '';
    newAgent.pending = null;
    newAgent.sending = false;
    modelOptions = {};
    recentCwds.clear();
    $('console').hidden = true;
    $('login').hidden = false;
    setStatus(message, 'error');
  }

  // Token usage: {input, output, cache_read, cache_write, total} | null | undefined (unknown).
  function fmtTokens(n) {
    if (n < 1000) return String(n);
    if (n < 1e6) return (n / 1000).toFixed(n < 1e4 ? 1 : n < 1e5 ? 1 : 0).replace(/\.0$/, '') + 'k';
    return (n / 1e6).toFixed(n < 1e7 ? 2 : 1).replace(/\.?0+$/, '') + 'M';
  }
  function tokenTip(u, extra) {
    if (!u) return 'usage unknown';
    const part = (label, n) => label + ' ' + fmtTokens(n || 0);
    return [part('input', u.input), part('output', u.output), part('cache read', u.cache_read), part('cache write', u.cache_write)].join(' · ')
      + (extra ? ' — ' + extra : '');
  }
  function usageSum(jobs) {
    const seen = new Set();
    const sum = { input: 0, output: 0, cache_read: 0, cache_write: 0, total: 0 };
    let any = false;
    for (const j of jobs) {
      const u = j.session_tokens;
      if (!u || !j.session_id || seen.has(j.session_id)) continue;
      seen.add(j.session_id);
      any = true;
      for (const k of Object.keys(sum)) sum[k] += u[k] || 0;
    }
    return any ? sum : null;
  }
  function addUsage(a, b) {
    if (!a) return b;
    if (!b) return a;
    const sum = {};
    for (const k of ['input', 'output', 'cache_read', 'cache_write', 'total']) sum[k] = (a[k] || 0) + (b[k] || 0);
    return sum;
  }
  function tokenSpan(cls, prefix, u, suffix, tip) {
    const node = element('span', cls, prefix + (u ? fmtTokens(u.total || 0) + (suffix || '') : '—'));
    node.title = tokenTip(u, tip);
    return node;
  }

  function element(tag, cls, value) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (value != null) node.textContent = String(value);
    return node;
  }

  // Markdown tree (AtfLib.parseMarkdown) -> DOM. Tags come from this fixed table, never from input.
  const MD_TAGS = { p: 'p', ul: 'ul', ol: 'ol', li: 'li', code: 'code', pre: 'pre', table: 'table',
    tr: 'tr', th: 'th', td: 'td', strong: 'strong', em: 'em', a: 'a' };

  function appendMarkdown(parent, nodes) {
    for (const n of nodes) {
      if (n.t === 'text') { parent.append(document.createTextNode(n.v)); continue; }
      if (n.t === 'br') { parent.append(document.createElement('br')); continue; }
      const tag = n.t === 'h' ? 'h' + Math.min(6, Math.max(3, n.level + 2)) : MD_TAGS[n.t];
      if (!tag) { parent.append(document.createTextNode('')); continue; }
      const el = document.createElement(tag);
      if (n.t === 'a') {
        const href = AtfLib.safeHref(n.href || '');
        if (!href) { parent.append(document.createTextNode(n.c.map(c => c.v || '').join(''))); continue; }
        el.href = href;
        el.rel = 'noopener noreferrer';
        el.target = '_blank';
      }
      if (n.v != null) el.textContent = n.v;
      if (n.c) appendMarkdown(el, n.c);
      if (n.t === 'table') {
        const wrap = document.createElement('div');
        wrap.className = 'md-scroll';
        wrap.append(el);
        parent.append(wrap);
      } else parent.append(el);
    }
  }

  function renderMarkdown(container, text) {
    container.textContent = '';
    appendMarkdown(container, AtfLib.parseMarkdown(text));
  }

  // Rendered markdown with a per-bubble Raw toggle (client state only).
  function markdownView(text) {
    const wrap = element('div', 'md-view');
    const toggle = element('button', 'md-raw-toggle', 'Raw');
    toggle.type = 'button';
    toggle.setAttribute('aria-pressed', 'false');
    const body = element('div', 'md');
    renderMarkdown(body, text);
    let raw = false;
    toggle.addEventListener('click', () => {
      raw = !raw;
      toggle.setAttribute('aria-pressed', String(raw));
      toggle.textContent = raw ? 'Rendered' : 'Raw';
      if (raw) { body.textContent = ''; body.append(element('pre', '', text)); }
      else renderMarkdown(body, text);
    });
    wrap.append(toggle, body);
    return wrap;
  }

  function age(when) {
    const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(when)) / 1000));
    if (!Number.isFinite(seconds)) return 'unknown';
    if (seconds < 60) return seconds + 's';
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return minutes + 'm';
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return hours + 'h ' + (minutes % 60) + 'm';
    return Math.floor(hours / 24) + 'd ' + (hours % 24) + 'h';
  }

  function backendChip(backend) {
    const names = { claude: 'Claude Code', codex: 'Codex', pi: 'Pi', fake: 'Fake' };
    const chip = element('span', 'chip backend-' + (Object.hasOwn(names, backend) ? backend : 'other'), names[backend] || backend);
    return chip;
  }

  function light(name, label) {
    const span = document.createElement('span');
    span.className = 'light ' + name;
    const dot = document.createElement('span');
    dot.className = 'dot';
    dot.setAttribute('aria-hidden', 'true');
    span.append(dot, element('span', 'state-label', label));
    return span;
  }

  function composerState(key) {
    if (!composers.has(key)) composers.set(key, {
      draft: '', interrupt: false, targetJobId: null, pending: null, sending: false,
      result: '', resultClass: '', deliveryJobId: null, resultNode: null,
    });
    return composers.get(key);
  }

  async function loadConfig() {
    const r = await api('GET', '/api/config');
    modelOptions = r?.model_options || {};
    tierSettings = r?.tiers || tierSettings;
    herdrMode = !!r?.herdr_mode;
    defaultPlacement = r?.herdr_placement || 'herdr-session:default';
    $('new-agent-session-field').hidden = !herdrMode;
    $('herdr-settings').hidden = !herdrMode;
    setPlacementControls('new-agent', defaultPlacement);
    setPlacementControls('settings', defaultPlacement);
    const select = $('new-agent-backend');
    select.replaceChildren();
    for (const backend of (r?.backends || []).filter(name => ['claude', 'codex', 'pi', 'cursor', 'droid'].includes(name))) {
      const option = element('option', '', ({ claude: 'Claude Code', codex: 'Codex', pi: 'Pi', cursor: 'Cursor CLI', droid: 'Factory Droid' })[backend]);
      option.value = backend;
      select.append(option);
    }
    if (!select.options.length) {
      select.append(element('option', '', 'No configured agent backends'));
      select.disabled = true;
      $('new-agent-submit').disabled = true;
    }
    syncModelOptions();
    newAgentControls();
  }

  function syncModelOptions() {
    const backend = $('new-agent-backend').value;
    const choices = modelOptions[backend] || { models: [], efforts: [] };
    const model = $('new-agent-model');
    const prior = model.value;
    model.replaceChildren();
    for (const value of choices.models) {
      const mapping = tierSettings.find(row => row.backend === backend && row.tier === value);
      const label = mapping ? `${value} — ${mapping.model} / ${mapping.effort}${mapping.custom ? ' (custom)' : ''}` : value;
      const option = element('option', '', label);
      option.value = value;
      model.append(option);
    }
    model.value = choices.models.includes(prior) ? prior :
      (choices.models.includes('medium') ? 'medium' : choices.models[0] || '');
    const effort = $('new-agent-effort');
    effort.replaceChildren();
    const defaultOption = element('option', '', 'Backend default');
    defaultOption.value = '';
    effort.append(defaultOption);
    for (const value of choices.efforts) {
      const option = element('option', '', value);
      option.value = value;
      effort.append(option);
    }
    $('new-agent-effort-field').hidden = !choices.efforts.length;
    effort.disabled = !choices.efforts.length || newAgent.sending || !!newAgent.pending;
  }

  async function loadRetentionSettings() {
    const r = await api('GET', '/api/settings/retention');
    if (!r?.ok) { $('retention-status').textContent = r?.error || 'Could not load idle settings.'; return; }
    const settings = r.retention_settings;
    $('settings-max-retained').value = settings.max_retained_sessions;
    $('settings-idle-off').checked = settings.idle_close_minutes === -1;
    $('settings-idle-minutes').disabled = $('settings-idle-off').checked;
    $('settings-idle-minutes').value = settings.idle_close_minutes === -1 ? 5 : settings.idle_close_minutes;
  }

  async function loadTierSettings() {
    const r = await api('GET', '/api/settings/tiers');
    if (!r?.ok) { $('settings-status').textContent = r?.error || 'Could not load settings.'; return; }
    tierSettings = r.tiers || [];
    renderTierSettings(r.model_catalog || {});
  }

  function setPlacementControls(prefix, value) {
    $(prefix + '-session').value = value.startsWith('herdr-session:') ? value.slice(14) : 'default';
  }

  function chosenPlacement(prefix) {
    return 'herdr-session:' + $(prefix + '-session').value.trim();
  }

  function renderTierSettings(catalog) {
    const target = $('tier-settings');
    target.replaceChildren();
    for (const backend of ['codex', 'pi', 'cursor', 'droid']) {
      const section = element('section', 'tier-backend');
      section.append(element('h3', '', ({ codex: 'Codex', pi: 'Pi', cursor: 'Cursor CLI', droid: 'Factory Droid' })[backend]));
      const table = element('table', 'tier-table');
      const head = element('thead');
      const headings = element('tr');
      for (const label of ['Tier', 'Model', 'Effort', 'Default', '']) headings.append(element('th', '', label));
      head.append(headings); table.append(head);
      const body = element('tbody');
      for (const row of tierSettings.filter(item => item.backend === backend)) {
        const tr = element('tr', row.custom ? 'custom' : '');
        const name = element('td', '', row.tier + (row.custom ? ' · custom' : ''));
        name.dataset.label = 'Tier'; tr.append(name);
        const modelCell = element('td'); modelCell.dataset.label = 'Model';
        const known = catalog[backend] || [];
        const model = element(known.length ? 'select' : 'input');
        if (known.length) {
          for (const value of [...new Set([...known, row.model])].sort()) {
            const option = element('option', '', value); option.value = value; model.append(option);
          }
          model.value = row.model;
        } else { model.type = 'text'; model.maxLength = 128; model.value = row.model; }
        model.setAttribute('aria-label', `${backend} ${row.tier} model`);
        modelCell.append(model); tr.append(modelCell);
        const effortCell = element('td'); effortCell.dataset.label = 'Effort';
        const effort = element('select'); effort.setAttribute('aria-label', `${backend} ${row.tier} effort`);
        const levels = backend === 'pi' ? ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']
          : backend === 'droid' ? ['none', 'dynamic', 'off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']
          : backend === 'cursor' ? ['none'] : ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'];
        for (const value of levels) { const option = element('option', '', value); option.value = value; effort.append(option); }
        effort.value = row.effort; effortCell.append(effort); tr.append(effortCell);
        const defaults = element('td', 'tier-default', `${row.default_model} / ${row.default_effort}`);
        defaults.dataset.label = 'Default'; tr.append(defaults);
        const actions = element('td', 'tier-actions');
        const save = element('button', '', 'Save'); save.type = 'button';
        save.addEventListener('click', async () => {
          save.disabled = true;
          const result = await api('PUT', '/api/settings/tiers', { backend, tier: row.tier, model: model.value.trim(), effort: effort.value });
          $('settings-status').textContent = result?.ok ? `${backend} ${row.tier} saved.` : (result?.error || 'Save failed.');
          if (result?.ok) { await loadTierSettings(); await loadConfig(); }
          save.disabled = false;
        });
        const reset = element('button', '', 'Reset'); reset.type = 'button'; reset.disabled = !row.custom;
        reset.addEventListener('click', async () => {
          const result = await api('PUT', '/api/settings/tiers', { backend, tier: row.tier });
          $('settings-status').textContent = result?.ok ? `${backend} ${row.tier} reset.` : (result?.error || 'Reset failed.');
          if (result?.ok) { await loadTierSettings(); await loadConfig(); }
        });
        actions.append(save, reset); tr.append(actions); body.append(tr);
      }
      table.append(body); section.append(table); target.append(section);
    }
  }

  function renderRecentCwds() {
    const list = $('new-agent-recent-list');
    list.replaceChildren();
    for (const path of recentCwds.keys()) {
      const button = element('button', '', path);
      button.type = 'button';
      button.addEventListener('click', () => { $('new-agent-cwd').value = path; });
      list.append(button);
    }
    $('new-agent-recent').hidden = !recentCwds.size;
  }

  async function browseDirectory(path) {
    const status = $('new-agent-picker-status');
    status.textContent = 'Loading…';
    const r = await api('GET', '/api/directories?path=' + encodeURIComponent(path));
    if (!r || r.error || !r.path) {
      status.textContent = 'Cannot list that directory.';
      return;
    }
    pickedDirectory = r.path;
    status.textContent = '';
    const crumbs = $('new-agent-breadcrumb');
    crumbs.replaceChildren();
    const parts = r.path.split('/').filter(Boolean);
    let location = '';
    const paths = ['/'];
    for (const part of parts) { location += '/' + part; paths.push(location); }
    paths.forEach((partPath, index) => {
      const button = element('button', '', index === 0 ? '/' : parts[index - 1]);
      button.type = 'button';
      button.addEventListener('click', () => browseDirectory(partPath));
      crumbs.append(button);
    });
    const list = $('new-agent-directory-list');
    list.replaceChildren();
    for (const child of r.directories || []) {
      const button = element('button', '', child.name + '/');
      button.type = 'button';
      button.addEventListener('click', () => browseDirectory(child.path));
      list.append(button);
    }
    if (!list.children.length) list.append(element('span', '', 'No subdirectories'));
    $('new-agent-picker').hidden = false;
  }

  function syncLeadOptions() {
    const key = JSON.stringify([[...knownLeads], [...leadNames]]);
    if (key === leadOptionsKey) return;
    leadOptionsKey = key;
    const select = $('new-agent-lead');
    const selected = select.value;
    select.replaceChildren();
    const none = element('option', '', 'No lead session');
    none.value = '';
    select.append(none);
    for (const [id, workspace] of knownLeads) {
      const option = element('option', '', (leadNames.get(id) || id.slice(0, 8)) + ' · ' + workspace);
      option.value = id;
      select.append(option);
    }
    select.value = knownLeads.has(selected) ? selected : '';
  }

  function newAgentControls() {
    const busy = newAgent.sending || !!newAgent.pending;
    for (const control of $('new-agent-form').querySelectorAll('input, select, textarea')) control.disabled = busy;
    $('new-agent-effort').disabled = busy || $('new-agent-effort-field').hidden;
    $('new-agent-browse').disabled = busy;
    $('new-agent-use-dir').disabled = busy;
    $('new-agent-backend').disabled = busy || !$('new-agent-backend').options.length
      || !['claude', 'codex', 'pi'].includes($('new-agent-backend').value);
    $('new-agent-submit').disabled = busy || $('new-agent-backend').disabled;
    $('new-agent-retry').hidden = !newAgent.pending || newAgent.sending;
    $('new-agent-discard').hidden = !newAgent.pending || newAgent.sending;
  }

  async function submitNewAgent() {
    if (newAgent.sending) return;
    if (!newAgent.pending) {
      const leadId = $('new-agent-lead').value || null;
      newAgent.pending = {
        backend: $('new-agent-backend').value,
        name: $('new-agent-name').value.trim() || null,
        model: $('new-agent-model').value.trim() || null,
        effort: $('new-agent-effort').value.trim() || null,
        herdr_placement: herdrMode ? chosenPlacement('new-agent') : null,
        cwd: $('new-agent-cwd').value.trim(),
        lead_session_id: leadId,
        workspace: leadId ? knownLeads.get(leadId) : null,
        instruction: $('new-agent-prompt').value,
        idempotency_key: crypto.randomUUID(),
      };
    }
    newAgent.sending = true;
    newAgentControls();
    const result = $('new-agent-result');
    result.textContent = 'Submitting…';
    result.className = '';
    const r = await api('POST', '/api/jobs', newAgent.pending);
    newAgent.sending = false;
    if (!token) return;
    if (!r || r.lost || r.error === 'outcome_unknown') {
      result.textContent = 'Outcome unknown. Retry with the same key or discard this attempt.';
      result.className = 'warn';
    } else if (r.ok) {
      result.textContent = 'Accepted · job ' + (r.job?.job_id || 'unknown');
      result.className = '';
      newAgent.pending = null;
      $('new-agent-prompt').value = '';
      await loadJobs();
    } else if (r.error === 'daemon_unavailable' || r.error === 'web_busy') {
      result.textContent = 'Not submitted (' + r.error + '). Retry with the same key or discard.';
      result.className = 'warn';
    } else {
      result.textContent = r.error === 'invalid_request' || r.error === 'web_bad_request'
        ? 'Rejected: check the name, model, effort, and existing absolute directory.'
        : 'Rejected: ' + r.error;
      result.className = 'error';
      newAgent.pending = null;
    }
    newAgentControls();
  }

  function composer(container, key, targets, lead = false, stopTarget = null, leadSession = null) {
    const state = composerState(key);
    // A real lead (with a workspace) can be messaged directly; it is the default target.
    const leadInbox = lead && leadSession?.workspace ? leadSession : null;
    if (!(leadInbox && state.targetJobId === LEAD_TARGET) && !targets.some(j => j.job_id === state.targetJobId)) {
      state.targetJobId = leadInbox ? LEAD_TARGET : targets[0]?.job_id || null;
    }
    const toLead = !!leadInbox && state.targetJobId === LEAD_TARGET;
    const target = toLead ? undefined : targets.find(j => j.job_id === state.targetJobId);
    const canSend = toLead || !!target;
    if (target?.status !== 'running') state.interrupt = false;
    const form = element('form', 'composer');
    form.autocomplete = 'off';
    const heading = element('div', 'composer-target');
    if (lead && (leadInbox ? targets.length > 0 : targets.length > 1)) {
      const label = element('label', '', leadInbox ? 'Message ' : 'Message member ');
      const picker = element('select', 'composer-picker');
      picker.dataset.composerKey = key;
      picker.dataset.composerRole = 'target';
      let options = picker;
      if (leadInbox) {
        const inbox = element('option', '', 'Lead session (inbox)');
        inbox.value = LEAD_TARGET;
        picker.append(inbox);
        options = element('optgroup');
        options.label = 'Message member';
        picker.append(options);
      }
      for (const j of targets) {
        const option = element('option', '', agentName(j) + ' · ' + j.job_id.slice(-8) + ' · ' + j.status);
        option.value = j.job_id;
        options.append(option);
      }
      picker.value = state.targetJobId;
      picker.disabled = state.sending || !!state.pending;
      picker.addEventListener('change', () => {
        state.targetJobId = picker.value;
        const chosen = targets.find(j => j.job_id === picker.value);
        interrupt.disabled = chosen?.status !== 'running';
        if (interrupt.disabled) { interrupt.checked = false; state.interrupt = false; }
        stop.disabled = !chosen || (chosen.status !== 'queued' && chosen.status !== 'running' && chosen.backend === 'fake');
        loadJobs();
      });
      label.append(picker);
      heading.append(label);
    } else {
      heading.textContent = toLead ? '→ Lead session (inbox)'
        : target ? '→ ' + agentName(target) + ' · job ' + target.job_id.slice(-8)
        : lead ? 'No member agent session to message yet' : 'Agent session not available yet';
    }
    const input = element('textarea', 'composer-input');
    input.rows = 2;
    input.maxLength = 65536;
    input.placeholder = toLead ? 'Message the lead (lands in read_messages)…'
      : target ? 'Message this agent…' : 'Available after an agent session starts';
    input.setAttribute('aria-label', toLead ? 'Message lead session' : 'Message ' + (target ? agentName(target) : 'agent'));
    input.dataset.composerKey = key;
    input.dataset.composerRole = 'message';
    input.value = state.draft;
    input.disabled = !canSend || state.sending || !!state.pending;
    input.addEventListener('input', () => { state.draft = input.value; send.disabled = !state.draft.trim() || state.sending; });
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); form.requestSubmit(); }
    });
    const actions = element('div', 'composer-actions');
    const interruptLabel = element('label', 'interrupt-label');
    const interrupt = element('input');
    interrupt.type = 'checkbox';
    interrupt.checked = state.interrupt && target?.status === 'running';
    interrupt.disabled = !target || target.status !== 'running' || state.sending || !!state.pending;
    interrupt.addEventListener('change', () => { state.interrupt = interrupt.checked; });
    interruptLabel.append(interrupt, document.createTextNode('Interrupt'));
    const send = element('button', 'send-button', state.sending ? 'Sending…' : 'Send');
    send.type = 'submit';
    send.disabled = !canSend || !state.draft.trim() || state.sending || !!state.pending;
    const stop = element('button', 'danger-action', 'Stop');
    stop.type = 'button';
    const stoppable = stopTarget || target;
    stop.disabled = !stoppable || (stoppable.status !== 'queued' && stoppable.status !== 'running' && stoppable.backend === 'fake');
    stop.addEventListener('click', () => {
      const chosen = stopTarget || targets.find(j => j.job_id === state.targetJobId);
      if (chosen) stopJob(chosen.job_id, chosen.status, key);
    });
    const retry = element('button', '', 'Retry same message');
    retry.type = 'button';
    retry.hidden = !state.pending || state.sending;
    retry.addEventListener('click', () => sendInline(key));
    const discard = element('button', '', 'Discard');
    discard.type = 'button';
    discard.hidden = !state.pending || state.sending;
    discard.addEventListener('click', () => {
      state.pending = null;
      state.result = 'Message attempt discarded.';
      state.resultClass = 'warn';
      loadJobs();
    });
    actions.append(interruptLabel, send, stop, retry, discard);
    const result = element('p', 'composer-result ' + state.resultClass, state.result);
    result.setAttribute('role', 'status');
    state.resultNode = result;
    state.form = form;
    form.addEventListener('submit', e => {
      e.preventDefault();
      if (!state.pending && (!canSend || !state.draft.trim())) return;
      if (!state.pending) state.pending = {
        jobId: state.targetJobId, text: state.draft, interrupt: state.interrupt, key: crypto.randomUUID(),
        lead: toLead ? { id: leadInbox.id, workspace: leadInbox.workspace } : null,
      };
      sendInline(key);
    });
    form.append(heading, input, actions, result);
    container.append(form);
  }

  async function sendInline(key) {
    const state = composerState(key);
    if (!state.pending || state.sending) return;
    state.sending = true;
    state.result = 'Sending…';
    state.resultClass = '';
    if (state.form?.isConnected) {
      for (const control of state.form.querySelectorAll('textarea, select, input, button')) control.disabled = true;
      if (state.resultNode) state.resultNode.textContent = state.result;
    }
    const attempt = state.pending;
    const toLead = attempt.jobId === LEAD_TARGET && !!attempt.lead;
    const r = toLead
      ? await api('POST', '/api/leads/' + encodeURIComponent(attempt.lead.id) + '/messages',
        { text: attempt.text, workspace: attempt.lead.workspace, idempotency_key: attempt.key })
      : await api('POST', '/api/jobs/' + encodeURIComponent(attempt.jobId) + '/follow-up',
        { instruction: attempt.text, idempotency_key: attempt.key, interrupt: attempt.interrupt });
    state.sending = false;
    if (!token) return;
    if (!r || r.lost || r.error === 'outcome_unknown') {
      state.result = 'Delivery unknown. Retry with the same key or discard this attempt.';
      state.resultClass = 'warn';
    } else if (r.ok) {
      state.pending = null;
      state.draft = '';
      state.interrupt = false;
      // A lead-inbox message has no job to track.
      state.deliveryJobId = toLead ? null : r.job?.job_id || null;
      state.result = toLead ? 'Delivered to lead inbox'
        : 'Queued' + (state.deliveryJobId ? ' · job ' + state.deliveryJobId.slice(-8) : '');
      state.resultClass = '';
    } else if (r.error === 'daemon_unavailable' || r.error === 'web_busy') {
      state.result = 'Not sent (' + r.error + '). Retry with the same key or discard.';
      state.resultClass = 'warn';
    } else {
      state.pending = null;
      state.result = 'Failed: ' + r.error;
      state.resultClass = 'error';
    }
    await loadJobs();
  }

  async function refreshDeliveries() {
    for (const state of composers.values()) {
      if (!state.deliveryJobId) continue;
      const r = await api('GET', '/api/jobs/' + encodeURIComponent(state.deliveryJobId));
      if (!r?.ok || !r.job) continue;
      if (r.job.status === 'completed') {
        state.result = 'Delivered · job ' + state.deliveryJobId.slice(-8);
        state.resultClass = '';
        state.deliveryJobId = null;
      } else if (['failed', 'cancelled', 'needs_reconciliation'].includes(r.job.status)) {
        state.result = 'Failed · job ' + state.deliveryJobId.slice(-8) + ' (' + r.job.status.replaceAll('_', ' ') + ')';
        state.resultClass = 'error';
        state.deliveryJobId = null;
      }
      if (state.resultNode?.isConnected) {
        state.resultNode.textContent = state.result;
        state.resultNode.className = 'composer-result ' + state.resultClass;
      }
    }
  }

  function ticketState(leadId) {
    if (!tickets.has(leadId)) tickets.set(leadId, { name: '', busy: false, result: '', ticket: null });
    return tickets.get(leadId);
  }

  function ticketCountdown(leadId) {
    const ticket = ticketState(leadId).ticket;
    const seconds = ticket ? Math.max(0, Math.ceil((Date.parse(ticket.expires_at) - Date.now()) / 1000)) : 0;
    return seconds ? 'Expires in ' + Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0') : 'Expired';
  }

  function updateTicketCountdowns() {
    for (const node of document.querySelectorAll('.ticket-expiry')) node.textContent = ticketCountdown(node.dataset.leadId);
  }

  function copyable(container, label, value) {
    const field = element('div', 'ticket-copy');
    const input = element('textarea');
    input.readOnly = true;
    input.setAttribute('aria-label', label);
    input.rows = label === 'Ticket' ? 2 : 4;
    input.value = value;
    const copy = element('button', '', 'Copy');
    copy.type = 'button';
    copy.setAttribute('aria-label', 'Copy ' + label);
    copy.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(input.value);
        copy.textContent = 'Copied';
      } catch {
        input.select();
        copy.textContent = 'Select text to copy';
      }
    });
    field.append(element('span', '', label), input, copy);
    container.append(field);
  }

  function renderTicket(container, leadId) {
    container.replaceChildren();
    const ticket = ticketState(leadId).ticket;
    if (!ticket) return;
    const expiry = element('p', 'ticket-expiry', ticketCountdown(leadId));
    expiry.dataset.leadId = leadId;
    container.append(expiry);
    copyable(container, 'Ticket', ticket.token);
    const call = 'mcp__agentteamforge__join_team(session_id="' + ticket.session_id + '", token="' + ticket.token + '")';
    copyable(container, 'Claude Desktop · external-member MCP entry',
      'Join my AgentTeamForge team as ' + ticket.name + '. Call ' + call
      + '. This AgentTeamForge external team is separate from Codex built-in collaboration. Save member_token from the reply. Use mcp__agentteamforge__external_read(member_token=...) to read work and mcp__agentteamforge__external_send(member_token=..., text=...) to reply.');
    copyable(container, 'Codex Desktop · external-member MCP entry', ticket.join_prompt || call);
  }

  function joinTicketForm(container, leadId, workspace) {
    const state = ticketState(leadId);
    const form = element('form', 'join-ticket-form');
    form.autocomplete = 'off';
    form.append(element('h4', '', 'Invite a desktop agent'));
    const nameLabel = element('label', '', 'Member name ');
    const name = element('input');
    name.type = 'text';
    name.required = true;
    name.maxLength = 64;
    name.pattern = '[A-Za-z0-9_\\-]+';
    name.placeholder = 'desktop-agent';
    name.value = state.name;
    name.dataset.composerKey = 'ticket:' + leadId;
    name.dataset.composerRole = 'ticket-name';
    name.disabled = state.busy;
    name.addEventListener('input', () => { state.name = name.value; });
    nameLabel.append(name);
    const generate = element('button', '', state.busy ? 'Generating…' : 'Generate join ticket');
    generate.type = 'submit';
    generate.disabled = state.busy;
    const result = element('span', '', state.result);
    result.setAttribute('role', 'status');
    form.append(nameLabel, generate, result);
    const output = element('div', 'ticket-output');
    renderTicket(output, leadId);
    form.addEventListener('submit', async e => {
      e.preventDefault();
      if (state.busy || !name.value.trim()) return;
      state.busy = true;
      name.disabled = generate.disabled = true;
      result.textContent = 'Generating…';
      const r = await api('POST', '/api/leads/' + encodeURIComponent(leadId) + '/join-ticket',
        { name: name.value.trim(), workspace });
      state.busy = false;
      if (!token) return;
      name.disabled = generate.disabled = false;
      if (r?.ok && r.ticket) {
        state.ticket = r.ticket;
        state.result = 'Ticket ready for ' + r.ticket.name;
        renderTicket(output, leadId);
      } else {
        state.result = 'Ticket unavailable: ' + (r?.error || 'network');
      }
      result.textContent = state.result;
    });
    container.append(form, output);
  }

  function cardLogState(key) {
    if (!cardLogs.has(key)) cardLogs.set(key, { open: false, text: '', offset: 0, decoder: new TextDecoder(), busy: false, node: null });
    return cardLogs.get(key);
  }

  function cardLog(container, key, jobId) {
    const state = cardLogState(key);
    const section = element('details', 'card-logs');
    section.open = state.open;
    const heading = element('summary', '', 'Raw logs');
    const refresh = element('button', '', 'Read new logs');
    refresh.type = 'button';
    refresh.addEventListener('click', () => loadCardLogs(key, jobId));
    const output = element('pre', '', state.text);
    state.node = output;
    section.addEventListener('toggle', () => {
      state.open = section.open;
      if (section.open) loadCardLogs(key, jobId);
    });
    section.append(heading, refresh, output);
    container.append(section);
  }

  function activityState(jobId) {
    if (!activities.has(jobId)) activities.set(jobId, {
      entries: [], cursor: 0, busy: false, complete: false, omitted: 0, nodes: new Set(), error: '',
    });
    return activities.get(jobId);
  }

  function activityPanel(container, jobId) {
    const section = element('section', 'activity-transcript');
    section.append(element('h4', '', 'Activity'));
    const list = element('ol', 'activity-entries');
    list.setAttribute('aria-label', 'Agent activity');
    const state = activityState(jobId);
    state.nodes.add(list);
    renderActivity(state, list);
    section.append(list);
    container.append(section);
  }

  // Scroll position of a node that is about to be rewritten; a node parked at the bottom follows new content.
  function scrollState(node) {
    const top = node.scrollTop;
    return { top, atBottom: node.scrollHeight > node.clientHeight + 4 && top + node.clientHeight >= node.scrollHeight - 4 };
  }

  function restoreScroll(node, saved) {
    node.scrollTop = saved.atBottom ? node.scrollHeight : saved.top;
  }

  function keepScroll(node, update) {
    const saved = scrollState(node);
    update();
    restoreScroll(node, saved);
  }

  function renderActivity(state, list) {
    keepScroll(list, () => renderActivityEntries(state, list));
  }

  function renderActivityEntries(state, list) {
    list.replaceChildren();
    if (state.omitted) list.append(element('li', 'activity-omitted', state.omitted + ' earlier entries omitted; raw logs remain available.'));
    for (const entry of state.entries) {
      const kind = ['assistant_text', 'tool_call', 'tool_result', 'status', 'error', 'result'].includes(entry.kind) ? entry.kind : 'status';
      const item = element('li', 'activity-entry kind-' + kind);
      const when = new Date(entry.ts);
      if (Number.isFinite(when.getTime())) {
        const time = element('time', 'activity-time', when.toLocaleTimeString());
        time.dateTime = entry.ts;
        item.append(time);
      }
      item.append(element('span', 'activity-kind', kind.replaceAll('_', ' ')), element('span', 'activity-text', entry.text));
      list.append(item);
    }
    if (!state.entries.length) list.append(element('li', 'activity-empty', state.error || 'No activity yet.'));
    else if (state.error) list.append(element('li', 'activity-error', state.error));
  }

  async function loadActivity(jobId, status) {
    const state = activityState(jobId);
    if (state.busy || state.complete) return;
    state.busy = true;
    try {
      for (let page = 0; page < 10; page++) {
        const before = state.cursor;
        const r = await api('GET', '/api/jobs/' + encodeURIComponent(jobId) + '/activity?after_cursor=' + before + '&limit=20');
        if (!r?.ok || !r.activity) {
          state.error = 'Activity unavailable: ' + (r?.error || 'network');
          break;
        }
        const entries = r.activity.entries || [];
        const next = r.activity.next_cursor;
        if (!Number.isSafeInteger(next) || next < before) {
          state.error = 'Activity cursor unavailable.';
          break;
        }
        state.error = '';
        state.cursor = next;
        state.entries.push(...entries);
        if (state.entries.length > 500) {
          const excess = state.entries.length - 500;
          state.entries.splice(0, excess);
          state.omitted += excess;
        }
        state.nodes = new Set([...state.nodes].filter(node => node.isConnected));
        for (const node of state.nodes) renderActivity(state, node);
        if (entries.length < 20 || next === before) {
          if (status !== 'queued' && status !== 'running') state.complete = true;
          break;
        }
      }
    } finally {
      state.busy = false;
      state.nodes = new Set([...state.nodes].filter(node => node.isConnected));
      for (const node of state.nodes) renderActivity(state, node);
    }
  }

  function cardDetail(container, jobId) {
    const section = element('section', 'card-result');
    section.append(element('h4', '', 'Result'));
    const meta = element('p', 'result-meta');
    const output = element('div', 'result-body');
    const cached = jobDetails.get(jobId);
    if (cached) showDetail(cached, meta, output);
    else output.textContent = 'Loading result…';
    section.append(meta, output);
    container.append(section);
    return () => loadCardDetail(jobId, meta, output);
  }

  function showDetail(job, meta, output) {
    meta.textContent = [job.status, job.reason_code, job.backend, job.session_id,
      job.parent_job_id ? 'parent ' + job.parent_job_id : null,
      job.cwd, 'attempts ' + job.attempts].filter(Boolean).join(' · ');
    // Render once per result: polls with an unchanged result keep the DOM (and the Raw toggle).
    const signature = job.job_id + ':' + (job.result == null ? '-' : job.result.length);
    if (output.dataset.signature === signature && output.childNodes.length > 0) return;
    output.dataset.signature = signature;
    keepScroll(output, () => {
      if (job.result) { output.textContent = ''; output.append(markdownView(job.result)); return; }
      output.textContent = job.result == null
        ? (job.status === 'queued' || job.status === 'running' ? 'No result yet.' : 'Result unavailable.')
        : '(empty result)';
    });
  }

  async function loadCardDetail(jobId, meta, output) {
    const r = await api('GET', '/api/jobs/' + encodeURIComponent(jobId));
    if (!r?.ok || !r.job) {
      if (output.isConnected) output.textContent = 'Result unavailable: ' + (r?.error || 'network');
      return;
    }
    jobDetails.set(jobId, r.job);
    if (output.isConnected) showDetail(r.job, meta, output);
  }

  async function loadCardLogs(key, jobId) {
    const state = cardLogState(key);
    if (state.busy) return;
    state.busy = true;
    try {
      for (let page = 0; page < 160; page++) {
        const r = await api('GET', '/api/jobs/' + encodeURIComponent(jobId) + '/output?offset=' + state.offset);
        if (!r?.ok || !r.output) {
          state.text += '\n[Logs unavailable: ' + (r?.error || 'network') + ']';
          break;
        }
        if (r.output.truncated) {
          state.text += '\n[Earlier log bytes were trimmed]\n';
          state.decoder = new TextDecoder();
        }
        const raw = atob(r.output.data_base64 || '');
        state.text += state.decoder.decode(Uint8Array.from(raw, c => c.charCodeAt(0)), { stream: true });
        state.offset = r.output.next_offset;
        if (state.offset >= r.output.end_offset) break;
      }
      if (state.node?.isConnected) keepScroll(state.node, () => { state.node.textContent = state.text; });
    } finally {
      state.busy = false;
    }
  }

  const panelId = (key) => 'panel-' + key.replaceAll(/[^a-zA-Z0-9-]/g, '-');

  function cardPanel(card, key, targets, lead = false, stopTarget = null, leadSession = null) {
    const panel = element('div', 'card-expanded');
    panel.dataset.expandKey = key;
    panel.id = panelId(key);
    panel.ownerCard = card;
    panel.hidden = expandedKey !== key;
    if (!stopTarget?.connector) composer(panel, key, targets, lead, stopTarget, leadSession);
    if (leadSession?.workspace) joinTicketForm(panel, leadSession.id, leadSession.workspace);
    const chosen = composerState(key).targetJobId;
    // With the lead inbox chosen there is no job of its own: show the first member's details.
    const jobId = (chosen === LEAD_TARGET ? targets[0]?.job_id : chosen) || stopTarget?.job_id;
    if (jobId) {
      const job = targets.find(j => j.job_id === jobId) || stopTarget;
      const refreshDetail = cardDetail(panel, jobId);
      activityPanel(panel, jobId);
      const logKey = key + ':logs:' + jobId;
      cardLog(panel, logKey, jobId);
      panel.openCard = () => {
        refreshDetail();
        loadActivity(jobId, job?.status);
        if (cardLogState(logKey).open) loadCardLogs(logKey, jobId);
      };
    }
    card.append(panel);
    return panel;
  }

  // `close` forces deselection; in wide mode clicking the selected card keeps it selected.
  function toggleCard(key, close = false) {
    const scroll = window.scrollY;
    const previous = expandedKey;
    expandedKey = expandedKey === key && (close || !wide.matches) ? null : key;
    for (const panel of document.querySelectorAll('.card-expanded')) {
      const open = panel.dataset.expandKey === expandedKey;
      panel.hidden = !open;
      panel.ownerCard.classList.toggle('selected', open);
      if (open) panel.openCard?.();
    }
    placeDetail();
    if (previous !== expandedKey) $('detail-pane').scrollTop = 0;
    window.scrollTo(0, scroll);
  }

  function findPanel(key, root = document) {
    if (!key) return null;
    return [...root.querySelectorAll('.card-expanded')].find(panel => panel.dataset.expandKey === key) || null;
  }

  // Selection buttons: accordion semantics when narrow, "current item" semantics when the pane is shown.
  function syncSelectionAria() {
    for (const button of document.querySelectorAll('#jobs [data-toggle-key]')) {
      const key = button.dataset.toggleKey;
      if (key.startsWith('team:')) continue;
      const open = key === expandedKey;
      if (wide.matches) {
        button.removeAttribute('aria-expanded');
        button.setAttribute('aria-controls', 'detail-pane');
        if (open) button.setAttribute('aria-current', 'true'); else button.removeAttribute('aria-current');
        button.setAttribute('aria-label', 'Show details for ' + button.dataset.toggleLabel);
      } else {
        button.removeAttribute('aria-current');
        button.setAttribute('aria-controls', panelId(key));
        button.setAttribute('aria-expanded', String(open));
        button.setAttribute('aria-label', (open ? 'Collapse ' : 'Expand ') + button.dataset.toggleLabel);
      }
    }
  }

  // Wide mode shows the selected panel in the side pane; otherwise every panel lives inside its card.
  function placeDetail() {
    const pane = $('detail-pane');
    const hint = pane.querySelector('.detail-empty');
    const held = pane.querySelector('.card-expanded');
    if (held) {
      if (held.ownerCard?.isConnected) {
        held.ownerCard.append(held);
        held.hidden = held.dataset.expandKey !== expandedKey;
      } else held.remove();
    }
    for (const node of [...pane.children]) if (node !== hint) node.remove();
    const panel = wide.matches ? findPanel(expandedKey, $('jobs')) : null;
    hint.hidden = !!panel;
    syncSelectionAria();
    if (!panel) return;
    const header = element('div', 'detail-header');
    const toggle = [...document.querySelectorAll('#jobs [data-toggle-key]')].find(b => b.dataset.toggleKey === expandedKey);
    header.append(element('h3', '', toggle?.dataset.toggleLabel || 'Details'));
    const closeButton = element('button', '', 'Close');
    closeButton.type = 'button';
    closeButton.addEventListener('click', () => toggleCard(expandedKey, true));
    header.append(closeButton);
    panel.hidden = false;
    pane.append(header, panel);
  }

  function teamOpen(key, needsAttention) {
    return Object.hasOwn(savedTeams, key) ? savedTeams[key] === true : needsAttention;
  }

  function toggleTeam(key, section) {
    const button = section.querySelector('.team-toggle');
    const body = section.querySelector('.team-content');
    const open = body.hidden;
    body.hidden = !open;
    button.setAttribute('aria-expanded', String(open));
    button.setAttribute('aria-label', (open ? 'Collapse ' : 'Expand ') + key.slice(5));
    saveTeams(key, open);
  }

  function setAllTeams(open) {
    for (const button of document.querySelectorAll('#jobs [data-toggle-key^="team:"]')) {
      delete savedTeams[button.dataset.toggleKey];
      savedTeams[button.dataset.toggleKey] = open;
    }
    saveTeams();
    loadJobs();
  }

  function connect(value) {
    token = value.trim();
    if (!token) return;
    sessionStorage.setItem(tokenKey, token);
    $('token').value = '';
    $('login').hidden = true;
    $('console').hidden = false;
    setStatus('connecting');
    clearInterval(timer);
    clearInterval(ticketTimer);
    timer = setInterval(loadJobs, 5000);
    ticketTimer = setInterval(updateTicketCountdowns, 1000);
    loadConfig();
    loadJobs();
  }

  const innerScrollers = ['.activity-entries', '.card-result pre', '.card-logs pre'];

  // The open panel is rebuilt on every poll; remember the pane and inner list scroll positions.
  function captureInnerScroll() {
    const panel = findPanel(expandedKey);
    if (!panel) return null;
    return {
      key: expandedKey,
      pane: $('detail-pane').scrollTop,
      nodes: innerScrollers.map(selector => {
        const node = panel.querySelector(selector);
        return node && node.clientHeight > 0 ? scrollState(node) : null;
      }),
    };
  }

  function restoreInnerScroll(saved) {
    if (!saved || saved.key !== expandedKey) return;
    const panel = findPanel(expandedKey);
    if (!panel) return;
    innerScrollers.forEach((selector, i) => {
      const node = panel.querySelector(selector);
      if (node && saved.nodes[i]) restoreScroll(node, saved.nodes[i]);
    });
    $('detail-pane').scrollTop = saved.pane;
  }

  async function loadJobs() {
    const params = new URLSearchParams();
    if ($('status-filter').value) params.set('status', $('status-filter').value);
    if (pageCursors[pageIndex]) params.set('cursor', pageCursors[pageIndex]);
    const seq = ++listSeq;
    const r = await api('GET', '/api/jobs' + (params.size ? '?' + params : ''));
    // Overlapping polls and actions: only the newest list request may render.
    if (!r || seq !== listSeq) return;
    if (!r.ok) {
      setStatus('list failed: ' + r.error, 'error');
      return;
    }
    setStatus('updated ' + new Date().toLocaleTimeString());
    const scroll = window.scrollY;
    const active = document.activeElement;
    const focusKey = active?.dataset?.composerKey || active?.dataset?.toggleKey;
    const focus = focusKey ? {
      key: focusKey, role: active.dataset.composerRole || 'toggle',
      start: active.selectionStart, end: active.selectionEnd,
    } : null;
    const overview = $('jobs');
    const scrolled = captureInnerScroll();
    overview.replaceChildren();
    nextCursor = r.page && r.page.has_more ? r.page.next_cursor : null;
    $('page-prev').disabled = pageIndex === 0;
    $('page-next').disabled = !nextCursor;
    $('page-number').textContent = 'Page ' + (pageIndex + 1);
    const jobs = (r.page && r.page.jobs) || [];
    for (const job of jobs) {
      if (job.cwd && !recentCwds.has(job.cwd)) recentCwds.set(job.cwd, true);
      if (recentCwds.size > 8) recentCwds.delete(recentCwds.keys().next().value);
    }
    renderRecentCwds();
    const counts = { yellow: 0, green: 0, red: 0, grey: 0 };
    const groups = new Map();
    for (const j of jobs) {
      const color = Object.hasOwn(counts, j.light) ? j.light : 'red';
      counts[color]++;
      const lead = j.connector ? 'PRFactory' : j.lead_session_id || 'No lead session';
      if (!j.connector && j.lead_session_id && j.lead_workspace) knownLeads.set(j.lead_session_id, j.lead_workspace);
      if (!j.connector && j.lead_session_id) {
        if (j.lead_name) leadNames.set(lead, j.lead_name);
        else leadNames.delete(lead);
      }
      if (!groups.has(lead)) groups.set(lead, []);
      groups.get(lead).push(j);
    }
    const leadTokens = r.lead_tokens || {};
    const sessionJobs = new Map();
    for (const j of jobs) if (j.session_id) sessionJobs.set(j.session_id, (sessionJobs.get(j.session_id) || 0) + 1);
    let totalUsage = usageSum(jobs);
    for (const u of Object.values(leadTokens)) totalUsage = addUsage(totalUsage, u);
    const tokenTotal = $('token-total');
    tokenTotal.textContent = totalUsage ? fmtTokens(totalUsage.total) : '—';
    tokenTotal.title = tokenTip(totalUsage, 'leads + distinct job sessions on this page');
    const membersByLead = new Map();
    for (const member of (r.external_members || [])) {
      if (member.workspace) knownLeads.set(member.lead_session_id, member.workspace);
      if (!membersByLead.has(member.lead_session_id)) membersByLead.set(member.lead_session_id, []);
      membersByLead.get(member.lead_session_id).push(member);
      if (!groups.has(member.lead_session_id)) groups.set(member.lead_session_id, []);
    }
    syncLeadOptions();
    const lights = $('lights');
    lights.replaceChildren();
    for (const [color, label] of [['green', 'Running'], ['yellow', 'Waiting'], ['red', 'Attention'], ['grey', 'Done / stopped']]) {
      lights.append(light(color, label + ' ' + counts[color]));
    }
    $('running-count').textContent = counts.green + ' of ' + jobs.length;
    const earliest = jobs.map(j => j.accepted_at).filter(Boolean).sort()[0];
    $('elapsed').textContent = earliest ? age(earliest) : '—';
    const recent = (a, b) => (b.updated_at || '').localeCompare(a.updated_at || '');
    for (const groupJobs of groups.values()) groupJobs.sort(recent);
    const sortedGroups = [...groups].sort((a, b) => recent(a[1][0] || {}, b[1][0] || {}));
    for (const [lead, groupJobs] of sortedGroups) {
      const section = element('section', 'lead-group');
      const members = membersByLead.get(lead) || [];
      const groupRunning = groupJobs.filter(j => j.status === 'running').length;
      const groupQueued = groupJobs.filter(j => j.status === 'queued').length + members.length;
      const groupFailed = groupJobs.filter(j => j.light === 'red').length;
      const groupColor = groupFailed ? 'red' : groupRunning ? 'green' : groupQueued ? 'yellow' : 'grey';
      const teamKey = 'team:' + lead;
      const isOpen = teamOpen(teamKey, groupFailed > 0 || groupRunning > 0 || groupQueued > 0);
      const hasLeadUsage = lead !== 'PRFactory' && lead !== 'No lead session' && Object.hasOwn(leadTokens, lead);
      const groupUsage = addUsage(usageSum(groupJobs), hasLeadUsage ? leadTokens[lead] : null);
      const teamToggle = element('button', 'team-toggle');
      teamToggle.type = 'button';
      teamToggle.dataset.toggleKey = teamKey;
      teamToggle.setAttribute('aria-expanded', String(isOpen));
      teamToggle.setAttribute('aria-label', (isOpen ? 'Collapse ' : 'Expand ') + lead);
      teamToggle.append(light(groupColor, groupFailed ? 'Needs attention' : groupRunning ? 'Running' : groupQueued ? 'Waiting' : 'Done'),
        element('strong', 'team-name', lead === 'PRFactory' ? 'PRFactory' : lead === 'No lead session' ? 'Unassigned' : 'Lead ' + (leadNames.get(lead) || lead.slice(0, 8))),
        ...(groupUsage ? [tokenSpan('team-tokens', 'Σ ', groupUsage, '', 'group: lead + distinct member sessions')] : []),
        element('span', 'team-counts', `${groupRunning} running · ${groupQueued} waiting · ${groupFailed} failed`),
        element('span', 'team-urgency badge ' + groupColor, groupFailed ? 'Needs attention' : groupRunning ? 'Running' : groupQueued ? 'Waiting' : 'Done'),
        element('span', 'team-chevron', isOpen ? '▾' : '▸'));
      const teamContent = element('div', 'team-content');
      teamContent.hidden = !isOpen;
      teamToggle.addEventListener('click', () => {
        toggleTeam(teamKey, section);
        teamToggle.querySelector('.team-chevron').textContent = teamContent.hidden ? '▸' : '▾';
      });
      section.append(teamToggle, teamContent);
      const leadKey = 'lead:' + lead;
      const leadCard = element('div', 'lead-card ' + (groupFailed ? 'is-failed' : groupQueued && !groupRunning ? 'is-waiting' : '')
        + (expandedKey === leadKey ? ' selected' : ''));
      const leadDot = light(groupColor, groupFailed ? 'Needs attention' : groupRunning ? 'Running' : groupQueued ? 'Waiting' : 'Done');
      leadDot.classList.add('lead-state');
      const leadBody = element('div', 'lead-body');
      const identity = element('div', 'node-identity');
      identity.append(element('h3', 'node-name', lead === 'No lead session' ? 'No lead session' : 'Lead session'),
        element('span', 'session-id', lead === 'No lead session' ? 'unassigned' : (leadNames.get(lead) || lead)));
      identity.querySelector('.session-id').title = lead;
      const activity = element('p', 'node-activity', groupRunning + ' running · ' + groupQueued + ' queued · ' + groupFailed + ' need attention · ' + groupJobs.length + ' jobs shown');
      const firstAccepted = groupJobs.map(j => j.accepted_at).filter(Boolean).sort()[0];
      const meta = element('div', 'node-meta');
      if (firstAccepted) meta.append(element('span', '', 'first shown job accepted ' + age(firstAccepted) + ' ago'));
      if (groupJobs[0]?.updated_at) meta.append(element('span', '', 'latest update ' + age(groupJobs[0].updated_at) + ' ago'));
      if (hasLeadUsage) meta.append(tokenSpan('tokens', 'lead ', leadTokens[lead], ' tok', 'lead own usage'));
      leadBody.append(identity, activity, meta);
      const leadToggle = element('button', 'lead-toggle');
      leadToggle.type = 'button';
      leadToggle.dataset.toggleKey = leadKey;
      leadToggle.dataset.toggleLabel = 'lead session ' + lead;
      leadToggle.setAttribute('aria-label', (expandedKey === leadKey ? 'Collapse' : 'Expand') + ' lead session ' + lead);
      leadToggle.setAttribute('aria-expanded', String(expandedKey === leadKey));
      leadToggle.addEventListener('click', () => toggleCard(leadKey));
      leadToggle.append(leadDot, leadBody, element('span', 'lead-count', groupJobs.length + (groupJobs.length === 1 ? ' job' : ' jobs')));
      leadCard.append(leadToggle);
      const leadTargets = groupJobs.filter(j => j.session_id);
      const leadWorkspace = groupJobs.find(j => j.lead_workspace)?.lead_workspace || members[0]?.workspace;
      const leadPanel = cardPanel(leadCard, leadKey, leadTargets, true, null,
        lead === 'No lead session' || lead === 'PRFactory' ? null : { id: lead, workspace: leadWorkspace });
      leadToggle.setAttribute('aria-controls', leadPanel.id);
      if (lead !== 'PRFactory') teamContent.append(leadCard);
      const tree = element('div', 'agent-tree');
      // Finished jobs fold away below the active ones.
      const isSettled = (j) => j.light === 'grey'
        && !['queued', 'running', 'parked', 'needs_reconciliation'].includes(j.status);
      const settledJobs = groupJobs.filter(isSettled);
      let settledList = null;
      let settledFold = null;
      if (settledJobs.length) {
        const foldKey = 'fold:' + lead;
        const selectedInside = settledJobs.some(j => 'job:' + j.job_id === expandedKey);
        const fold = element('details', 'settled-jobs');
        fold.dataset.foldKey = foldKey;
        let foldOpen = selectedInside || savedTeams[foldKey] === true;
        fold.open = foldOpen;
        fold.addEventListener('toggle', () => {
          if (fold.open === foldOpen) return;
          foldOpen = fold.open;
          saveTeams(foldKey, foldOpen);
        });
        fold.append(element('summary', '', settledJobs.length + ' finished'));
        settledList = element('div', 'settled-list');
        fold.append(settledList);
        settledFold = fold;
      }
      const ordered = settledJobs.length ? [...groupJobs.filter(j => !isSettled(j)), ...settledJobs] : groupJobs;
      for (const j of ordered) {
        const key = 'job:' + j.job_id;
        const card = element('article', 'agent-node' + (j.status === 'completed' ? ' settled' : '') + (key === expandedKey ? ' selected' : ''));
        const open = element('button', 'card-main');
        open.type = 'button';
        open.dataset.toggleKey = key;
        open.dataset.toggleLabel = 'job ' + j.job_id;
        open.setAttribute('aria-label', (key === expandedKey ? 'Collapse' : 'Expand') + ' job ' + j.job_id);
        open.setAttribute('aria-expanded', String(key === expandedKey));
        open.addEventListener('click', () => toggleCard(key));
        const row = element('span', 'card-identity');
        row.append(light(Object.hasOwn(counts, j.light) ? j.light : 'red', j.status.replaceAll('_', ' ')),
          element('strong', 'node-name', agentName(j)),
          element('span', 'job-id', 'job ' + j.job_id.slice(-8)));
        const chips = element('span', 'chips');
        if (j.backend) chips.append(backendChip(j.backend));
        if (j.model) chips.append(element('span', 'chip', j.model));
        if (j.effort) chips.append(element('span', 'chip subtle', 'effort ' + j.effort));
        if (herdrMode && j.herdr_placement) chips.append(element('span', 'chip subtle', j.herdr_placement === 'own-session' ? 'Own Herdr session' : 'Herdr ' + j.herdr_placement.slice(14)));
        const state = element('span', 'badge ' + (j.status === 'completed' ? 'done' : j.light),
          j.status === 'completed' ? 'done' : j.status === 'parked' ? 'parked · awaiting reply' : j.status.replaceAll('_', ' '));
        const cardMeta = element('span', 'card-meta');
        cardMeta.append(element('span', '', 'accepted ' + age(j.accepted_at) + ' ago'));
        if (j.agent_live === false) cardMeta.append(element('span', '', 'agent closed'));
        if (j.status !== 'running' && j.updated_at) cardMeta.append(element('span', '', 'updated ' + age(j.updated_at) + ' ago'));
        const preview = j.last_activity || j.reason_code;
        row.append(chips, state);
        open.append(row);
        if (j.startup) {
          const startup = j.startup.no_marker_since_launch ? 'no state marker since launch' : j.startup.phase;
          open.append(element('span', 'card-activity', 'Startup: ' + startup + ' · ' + j.startup.elapsed_seconds + 's'));
          if (j.startup.hint) open.append(element('span', 'card-activity', j.startup.hint));
        }
        if (preview) open.append(element('span', 'card-activity', '› ' + preview));
        open.append(cardMeta);
        const side = element('div', 'card-side');
        if (j.session_id && j.session_tokens !== undefined) {
          const shared = sessionJobs.get(j.session_id) || 1;
          side.append(tokenSpan('tokens', '', j.session_tokens, ' tok', shared > 1 ? 'session total (shared by ' + shared + ' jobs)' : ''));
        }
        side.append(element('span', 'elapsed', age(j.accepted_at)));
        if (j.status === 'running' && j.updated_at) side.append(element('span', 'beat', 'last update ' + age(j.updated_at) + ' ago'));
        card.append(open, side);
        if (herdrMode && j.herdr_placement) {
          const location = element('div', 'card-meta');
          if (j.herdr_placement === 'own-session') {
            if (j.herdr_session) {
              const command = 'herdr session attach ' + j.herdr_session;
              location.append(element('span', '', command));
              const copy = element('button', '', 'Copy'); copy.type = 'button';
              copy.addEventListener('click', () => navigator.clipboard.writeText(command));
              location.append(copy);
            } else location.append(element('span', '', 'Herdr session pending'));
          } else {
            location.append(element('span', '', 'Herdr session ' + (j.herdr_session || j.herdr_placement.slice(14))
              + (j.herdr_tab ? ' · tab ' + (j.herdr_tab_label || j.herdr_tab) + ' (' + j.herdr_tab + ')' : ' · tab pending')));
          }
          card.append(location);
        }
        const panel = cardPanel(card, key, j.session_id ? [j] : [], false, j);
        open.setAttribute('aria-controls', panel.id);
        (isSettled(j) ? settledList : tree).append(card);
      }
      if (settledFold) tree.append(settledFold);
      for (const member of members) {
        const node = element('article', 'agent-node external-node');
        node.append(light('yellow', 'External member'), element('strong', 'node-name', member.name),
          element('span', 'badge yellow', 'external · waiting'));
        tree.append(node);
      }
      teamContent.append(tree);
      overview.append(section);
    }
    if (!groups.size) overview.textContent = 'No jobs on this page.';
    // Wide: the pane shows the selection even when its team is collapsed; narrow: the panel must be visible inline.
    const openPanel = wide.matches ? findPanel(expandedKey, overview)
      : [...overview.querySelectorAll('.card-expanded')].find(panel => !panel.hidden && !panel.closest('.team-content')?.hidden);
    if (!openPanel) expandedKey = null;
    placeDetail();
    restoreInnerScroll(scrolled);
    if (focus) {
      const same = [...document.querySelectorAll('[data-composer-key], [data-toggle-key]')]
        .find(node => (node.dataset.composerKey || node.dataset.toggleKey) === focus.key
          && (node.dataset.composerRole || 'toggle') === focus.role && !node.disabled);
      if (same) {
        same.focus();
        if (focus.start != null && same.setSelectionRange) same.setSelectionRange(focus.start, focus.end);
      }
    }
    window.scrollTo(0, scroll);
    openPanel?.openCard?.();
    refreshDeliveries();
  }

  async function stopJob(jobId, status, cardKey) {
    const active = status === 'queued' || status === 'running' || status === 'needs_reconciliation';
    const action = active ? 'Stop job ' : 'Stop agent for job ';
    if (!window.confirm(action + jobId + '?')) return;
    const r = await api('POST', '/api/jobs/' + encodeURIComponent(jobId) + (active ? '/stop' : '/stop-agent'));
    if (!r) return;
    const message = r.lost || r.error === 'outcome_unknown'
      ? 'Stop outcome unknown; check the job status before trying again.'
      : r.ok
      ? 'Outcome: ' + (r.outcome || 'unknown') + '; status: ' + (r.job?.status || 'unknown')
        + '; reason_code: ' + (r.job?.reason_code || 'none')
      : 'Stop failed: ' + r.error + (r.error_detail ? ' — ' + r.error_detail : '');
    const state = cardKey && composerState(cardKey);
    if (state) {
      state.result = message;
      state.resultClass = r.ok ? '' : 'error';
    }
    setStatus(message, r.ok ? '' : 'error');
    await loadJobs();
  }

  document.addEventListener('DOMContentLoaded', () => {
    $('settings-toggle').addEventListener('click', async () => {
      $('overview-view').hidden = true;
      $('settings-view').hidden = false;
      $('settings-toggle').setAttribute('aria-expanded', 'true');
      await loadTierSettings();
      await loadRetentionSettings();
      if (herdrMode) {
        const placement = await api('GET', '/api/settings/herdr-placement');
        if (placement?.ok) setPlacementControls('settings', placement.herdr_placement);
      }
    });
    $('settings-idle-off').addEventListener('change', () => {
      $('settings-idle-minutes').disabled = $('settings-idle-off').checked;
    });
    $('retention-settings').addEventListener('submit', async event => {
      event.preventDefault();
      const button = event.currentTarget.querySelector('button');
      button.disabled = true;
      const r = await api('PUT', '/api/settings/retention', {
        max_retained_sessions: Number($('settings-max-retained').value),
        idle_close_minutes: $('settings-idle-off').checked ? 'off' : Number($('settings-idle-minutes').value),
      });
      $('retention-status').textContent = r?.ok ? 'Saved. Applies on the next idle sweep.' : r?.error || 'Could not save idle settings.';
      button.disabled = false;
    });
    $('settings-back').addEventListener('click', () => {
      $('settings-view').hidden = true;
      $('overview-view').hidden = false;
      $('settings-toggle').setAttribute('aria-expanded', 'false');
    });
    $('tiers-reset-all').addEventListener('click', async () => {
      const result = await api('PUT', '/api/settings/tiers', { reset_all: true });
      $('settings-status').textContent = result?.ok ? 'All tiers reset.' : (result?.error || 'Reset failed.');
      if (result?.ok) { await loadTierSettings(); await loadConfig(); }
    });
    $('settings-placement-save').addEventListener('click', async () => {
      const result = await api('PUT', '/api/settings/herdr-placement', { herdr_placement: chosenPlacement('settings') });
      $('settings-status').textContent = result?.ok ? 'Herdr placement saved.' : (result?.error || 'Save failed.');
      if (result?.ok) { defaultPlacement = result.herdr_placement; setPlacementControls('new-agent', defaultPlacement); }
    });
    $('theme-select').value = theme;
    $('theme-select').addEventListener('change', () => {
      theme = $('theme-select').value;
      applyTheme(theme);
      try { localStorage.setItem(themeKey, theme); } catch { /* Keep the in-tab choice. */ }
    });
    const fragment = new URLSearchParams(location.hash.slice(1));
    if (fragment.has('token')) {
      const fragmentToken = fragment.get('token');
      history.replaceState(null, '', location.pathname + location.search);
      if (fragmentToken) sessionStorage.setItem(tokenKey, fragmentToken);
    }
    $('login-form').addEventListener('submit', (e) => {
      e.preventDefault();
      connect($('token').value);
    });
    if (sessionStorage.getItem(tokenKey)) connect(sessionStorage.getItem(tokenKey));
    $('new-agent-toggle').addEventListener('click', () => {
      const form = $('new-agent-form');
      form.hidden = !form.hidden;
      $('new-agent-toggle').setAttribute('aria-expanded', String(!form.hidden));
      if (!form.hidden) $('new-agent-cwd').focus();
    });
    $('new-agent-form').addEventListener('submit', e => { e.preventDefault(); submitNewAgent(); });
    $('new-agent-backend').addEventListener('change', syncModelOptions);
    $('new-agent-browse').addEventListener('click', () => browseDirectory($('new-agent-cwd').value.trim() || '/'));
    $('new-agent-use-dir').addEventListener('click', () => {
      if (pickedDirectory) $('new-agent-cwd').value = pickedDirectory;
      $('new-agent-picker').hidden = true;
    });
    $('new-agent-close-picker').addEventListener('click', () => { $('new-agent-picker').hidden = true; });
    $('new-agent-retry').addEventListener('click', submitNewAgent);
    $('new-agent-discard').addEventListener('click', () => {
      newAgent.pending = null;
      $('new-agent-result').textContent = 'Submission attempt discarded.';
      newAgentControls();
    });
    $('refresh').addEventListener('click', loadJobs);
    $('collapse-all').addEventListener('click', () => setAllTeams(false));
    $('expand-all').addEventListener('click', () => setAllTeams(true));
    wide.addEventListener('change', placeDetail);
    $('status-filter').addEventListener('change', () => {
      pageCursors = [null];
      pageIndex = 0;
      loadJobs();
    });
    $('page-prev').addEventListener('click', () => { if (pageIndex > 0) { pageIndex--; loadJobs(); } });
    $('page-next').addEventListener('click', () => {
      if (nextCursor) { pageCursors[++pageIndex] = nextCursor; loadJobs(); }
    });
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && expandedKey) { e.preventDefault(); toggleCard(expandedKey, true); }
    });
  });
})();
