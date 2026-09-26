'use strict';
// Text-only operator console. All server text is rendered with textContent.
// A fragment token is copied to per-tab storage and removed from the address bar.
(() => {
  const $ = (id) => document.getElementById(id);
  const tokenKey = 'atf.web.token';
  let token = null;
  let expandedKey = null;
  let pageCursors = [null];
  let pageIndex = 0;
  let nextCursor = null;
  const composers = new Map();
  const cardLogs = new Map();
  const activities = new Map();
  const jobDetails = new Map();
  let timer = null;

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
    $('console').hidden = true;
    $('login').hidden = false;
    setStatus(message, 'error');
  }

  function element(tag, cls, value) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (value != null) node.textContent = String(value);
    return node;
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

  function composer(container, key, targets, lead = false, stopTarget = null) {
    const state = composerState(key);
    if (!targets.some(j => j.job_id === state.targetJobId)) state.targetJobId = targets[0]?.job_id || null;
    const target = targets.find(j => j.job_id === state.targetJobId);
    if (target?.status !== 'running') state.interrupt = false;
    const form = element('form', 'composer');
    form.autocomplete = 'off';
    const heading = element('div', 'composer-target');
    if (lead && targets.length > 1) {
      const label = element('label', '', 'Message member ');
      const picker = element('select', 'composer-picker');
      picker.dataset.composerKey = key;
      picker.dataset.composerRole = 'target';
      for (const j of targets) {
        const option = element('option', '', (j.target_agent || 'Agent') + ' · ' + j.job_id.slice(-8) + ' · ' + j.status);
        option.value = j.job_id;
        picker.append(option);
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
      heading.textContent = target ? '→ ' + (target.target_agent || 'Agent') + ' · job ' + target.job_id.slice(-8)
        : lead ? 'No member agent session to message yet' : 'Agent session not available yet';
    }
    const input = element('textarea', 'composer-input');
    input.rows = 2;
    input.maxLength = 8192;
    input.placeholder = target ? 'Message this agent…' : 'Available after an agent session starts';
    input.setAttribute('aria-label', 'Message ' + (target?.target_agent || 'agent'));
    input.dataset.composerKey = key;
    input.dataset.composerRole = 'message';
    input.value = state.draft;
    input.disabled = !target || state.sending || !!state.pending;
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
    send.disabled = !target || !state.draft.trim() || state.sending || !!state.pending;
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
      if (!state.pending && (!target || !state.draft.trim())) return;
      if (!state.pending) state.pending = {
        jobId: state.targetJobId, text: state.draft, interrupt: state.interrupt, key: crypto.randomUUID(),
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
    const r = await api('POST', '/api/jobs/' + encodeURIComponent(attempt.jobId) + '/follow-up',
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
      state.deliveryJobId = r.job?.job_id || null;
      state.result = 'Queued' + (state.deliveryJobId ? ' · job ' + state.deliveryJobId.slice(-8) : '');
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
    const section = element('section', 'card-activity');
    section.append(element('h4', '', 'Activity'));
    const list = element('ol', 'activity-entries');
    list.setAttribute('aria-label', 'Agent activity');
    const state = activityState(jobId);
    state.nodes.add(list);
    renderActivity(state, list);
    section.append(list);
    container.append(section);
  }

  function renderActivity(state, list) {
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
    const output = element('pre');
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
    output.textContent = job.result == null
      ? (job.status === 'queued' || job.status === 'running' ? 'No result yet.' : 'Result unavailable.')
      : job.result === '' ? '(empty result)' : job.result;
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
      if (state.node?.isConnected) state.node.textContent = state.text;
    } finally {
      state.busy = false;
    }
  }

  function cardPanel(card, key, targets, lead = false, stopTarget = null) {
    const panel = element('div', 'card-expanded');
    panel.dataset.expandKey = key;
    panel.id = 'panel-' + key.replaceAll(/[^a-zA-Z0-9-]/g, '-');
    panel.hidden = expandedKey !== key;
    composer(panel, key, targets, lead, stopTarget);
    const jobId = composerState(key).targetJobId || stopTarget?.job_id;
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

  function toggleCard(key) {
    const scroll = window.scrollY;
    expandedKey = expandedKey === key ? null : key;
    for (const panel of document.querySelectorAll('.card-expanded')) {
      const open = panel.dataset.expandKey === expandedKey;
      panel.hidden = !open;
      panel.parentElement.classList.toggle('selected', open);
      const button = panel.parentElement.querySelector('[data-toggle-key]');
      if (button) {
        button.setAttribute('aria-expanded', String(open));
        button.setAttribute('aria-label', (open ? 'Collapse ' : 'Expand ') + button.dataset.toggleLabel);
      }
      if (open) panel.openCard?.();
    }
    window.scrollTo(0, scroll);
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
    timer = setInterval(loadJobs, 5000);
    loadJobs();
  }

  async function loadJobs() {
    const params = new URLSearchParams();
    if ($('status-filter').value) params.set('status', $('status-filter').value);
    if (pageCursors[pageIndex]) params.set('cursor', pageCursors[pageIndex]);
    const r = await api('GET', '/api/jobs' + (params.size ? '?' + params : ''));
    if (!r) return;
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
    overview.replaceChildren();
    nextCursor = r.page && r.page.has_more ? r.page.next_cursor : null;
    $('page-prev').disabled = pageIndex === 0;
    $('page-next').disabled = !nextCursor;
    $('page-number').textContent = 'Page ' + (pageIndex + 1);
    const jobs = (r.page && r.page.jobs) || [];
    const counts = { yellow: 0, green: 0, red: 0, grey: 0 };
    const groups = new Map();
    for (const j of jobs) {
      const color = Object.hasOwn(counts, j.light) ? j.light : 'red';
      counts[color]++;
      const lead = j.lead_session_id || 'No lead session';
      if (!groups.has(lead)) groups.set(lead, []);
      groups.get(lead).push(j);
    }
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
    const sortedGroups = [...groups].sort((a, b) => recent(a[1][0], b[1][0]));
    for (const [lead, groupJobs] of sortedGroups) {
      const section = element('section', 'lead-group');
      const groupRunning = groupJobs.filter(j => j.status === 'running').length;
      const groupQueued = groupJobs.filter(j => j.status === 'queued').length;
      const groupFailed = groupJobs.filter(j => j.light === 'red').length;
      const groupColor = groupFailed ? 'red' : groupRunning ? 'green' : groupQueued ? 'yellow' : 'grey';
      const leadKey = 'lead:' + lead;
      const leadCard = element('div', 'lead-card ' + (groupFailed ? 'is-failed' : groupQueued && !groupRunning ? 'is-waiting' : '')
        + (expandedKey === leadKey ? ' selected' : ''));
      const leadDot = light(groupColor, groupFailed ? 'Needs attention' : groupRunning ? 'Running' : groupQueued ? 'Waiting' : 'Done');
      leadDot.classList.add('lead-state');
      const leadBody = element('div', 'lead-body');
      const identity = element('div', 'node-identity');
      identity.append(element('h3', 'node-name', lead === 'No lead session' ? 'No lead session' : 'Lead session'),
        element('span', 'session-id', lead === 'No lead session' ? 'unassigned' : lead));
      const activity = element('p', 'node-activity', groupRunning + ' running · ' + groupQueued + ' queued · ' + groupFailed + ' need attention · ' + groupJobs.length + ' jobs shown');
      const firstAccepted = groupJobs.map(j => j.accepted_at).filter(Boolean).sort()[0];
      const meta = element('div', 'node-meta');
      if (firstAccepted) meta.append(element('span', '', 'first shown job accepted ' + age(firstAccepted) + ' ago'));
      if (groupJobs[0].updated_at) meta.append(element('span', '', 'latest update ' + age(groupJobs[0].updated_at) + ' ago'));
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
      const leadPanel = cardPanel(leadCard, leadKey, leadTargets, true);
      leadToggle.setAttribute('aria-controls', leadPanel.id);
      section.append(leadCard);
      const tree = element('div', 'agent-tree');
      for (const j of groupJobs) {
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
          element('strong', 'node-name', j.target_agent || 'Agent'),
          element('span', 'job-id', 'job ' + j.job_id.slice(-8)));
        const chips = element('span', 'chips');
        if (j.backend) chips.append(backendChip(j.backend));
        if (j.model) chips.append(element('span', 'chip', j.model));
        if (j.effort) chips.append(element('span', 'chip subtle', 'effort ' + j.effort));
        const state = element('span', 'badge ' + (j.status === 'completed' ? 'done' : j.light),
          j.status === 'completed' ? 'done' : j.status === 'parked' ? 'parked · awaiting reply' : j.status.replaceAll('_', ' '));
        const cardMeta = element('span', 'card-meta');
        cardMeta.append(element('span', '', 'accepted ' + age(j.accepted_at) + ' ago'));
        const preview = j.last_activity || j.reason_code;
        row.append(chips, state);
        open.append(row);
        if (preview) open.append(element('span', 'card-last-activity', '› ' + preview));
        open.append(cardMeta);
        const side = element('div', 'card-side');
        side.append(element('span', 'elapsed', age(j.accepted_at)));
        if (j.updated_at) side.append(element('span', 'beat', 'updated ' + age(j.updated_at) + ' ago'));
        card.append(open, side);
        const panel = cardPanel(card, key, j.session_id ? [j] : [], false, j);
        open.setAttribute('aria-controls', panel.id);
        tree.append(card);
      }
      section.append(tree);
      overview.append(section);
    }
    if (!jobs.length) overview.textContent = 'No jobs on this page.';
    const openPanel = [...overview.querySelectorAll('.card-expanded')].find(panel => !panel.hidden);
    if (!openPanel) expandedKey = null;
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
    const active = status === 'queued' || status === 'running';
    const action = active ? 'Stop job ' : 'Stop agent for job ';
    if (!window.confirm(action + jobId + '?')) return;
    const r = await api('POST', '/api/jobs/' + encodeURIComponent(jobId) + (active ? '/stop' : '/stop-agent'));
    if (!r) return;
    const message = r.lost || r.error === 'outcome_unknown'
      ? 'Stop outcome unknown; check the job status before trying again.'
      : r.ok
      ? 'Outcome: ' + (r.outcome || 'unknown') + '; status: ' + (r.job?.status || 'unknown')
        + '; reason_code: ' + (r.job?.reason_code || 'none')
      : 'Stop failed: ' + r.error;
    const state = cardKey && composerState(cardKey);
    if (state) {
      state.result = message;
      state.resultClass = r.ok ? '' : 'error';
    }
    setStatus(message, r.ok ? '' : 'error');
    await loadJobs();
  }

  document.addEventListener('DOMContentLoaded', () => {
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
    $('refresh').addEventListener('click', loadJobs);
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
      if (e.key === 'Escape' && expandedKey) { e.preventDefault(); toggleCard(expandedKey); }
    });
  });
})();
