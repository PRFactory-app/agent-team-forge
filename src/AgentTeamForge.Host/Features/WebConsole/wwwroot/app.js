'use strict';
// Text-only operator console. All server text is rendered with textContent.
// A fragment token is copied to per-tab storage and removed from the address bar.
(() => {
  const $ = (id) => document.getElementById(id);
  const tokenKey = 'atf.web.token';
  let token = null;
  let selected = null;
  let selectedJob = null;
  let pageCursors = [null];
  let pageIndex = 0;
  let nextCursor = null;
  let logOffset = 0;
  let logDecoder = new TextDecoder();
  let logBusy = false;
  let logTimer = null;
  // A follow-up attempt keeps its idempotency key until a definitive answer.
  // After a lost/ambiguous response the operator decides: retry with the same key or discard.
  let pending = null;
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
    clearInterval(logTimer);
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
    span.append(dot, document.createTextNode(label));
    return span;
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
      const leadCard = element('div', 'lead-card ' + (groupFailed ? 'is-failed' : groupQueued && !groupRunning ? 'is-waiting' : ''));
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
      leadCard.append(leadDot, leadBody, element('span', 'lead-count', groupJobs.length + (groupJobs.length === 1 ? ' job' : ' jobs')));
      section.append(leadCard);
      const tree = element('div', 'agent-tree');
      for (const j of groupJobs) {
        const card = element('article', 'agent-node' + (j.job_id === selected ? ' selected' : ''));
        const open = element('button', 'card-main');
        open.type = 'button';
        open.setAttribute('aria-label', 'Open job ' + j.job_id);
        open.addEventListener('click', () => select(j.job_id));
        const row = element('span', 'card-identity');
        row.append(light(Object.hasOwn(counts, j.light) ? j.light : 'red', j.status.replaceAll('_', ' ')),
          element('strong', 'node-name', j.target_agent || 'Agent'),
          element('span', 'job-id', 'job ' + j.job_id.slice(-8)));
        const chips = element('span', 'chips');
        if (j.backend) chips.append(backendChip(j.backend));
        if (j.model) chips.append(element('span', 'chip', j.model));
        if (j.effort) chips.append(element('span', 'chip subtle', j.effort));
        const state = element('span', 'badge ' + (j.status === 'completed' ? 'done' : j.light),
          j.status === 'completed' ? 'done' : j.status.replaceAll('_', ' '));
        const cardMeta = element('span', 'card-meta');
        cardMeta.append(element('span', '', 'accepted ' + age(j.accepted_at) + ' ago'));
        if (j.updated_at) cardMeta.append(element('span', '', 'updated ' + age(j.updated_at) + ' ago'));
        if (j.reason_code) cardMeta.append(element('span', 'reason', '› ' + j.reason_code));
        open.append(row, chips, cardMeta);
        const side = element('div', 'card-side');
        side.append(element('span', 'elapsed', age(j.accepted_at)), state);
        const actions = element('div', 'card-actions');
        const logs = element('button', '', 'Logs');
        logs.type = 'button';
        logs.addEventListener('click', async () => { await select(j.job_id); $('d-logs').scrollIntoView({ block: 'nearest' }); });
        actions.append(logs);
        if (j.session_id) {
          const follow = element('button', '', 'Follow up');
          follow.type = 'button';
          follow.addEventListener('click', async () => {
            await select(j.job_id);
            $('follow-text').focus();
          });
          actions.append(follow);
        }
        if (j.status === 'queued' || j.status === 'running' || (j.session_id && j.backend !== 'fake')) {
          const stop = element('button', 'danger-action', j.status === 'queued' || j.status === 'running' ? 'Stop job' : 'Stop agent');
          stop.type = 'button';
          stop.addEventListener('click', () => stopJob(j.job_id, j.status));
          actions.append(stop);
        }
        side.append(actions);
        card.append(open, side);
        tree.append(card);
      }
      section.append(tree);
      overview.append(section);
    }
    if (!jobs.length) overview.textContent = 'No jobs on this page.';
  }

  async function select(jobId) {
    clearInterval(logTimer);
    logTimer = null;
    selected = jobId;
    selectedJob = null;
    logOffset = 0;
    logDecoder = new TextDecoder();
    $('d-logs').textContent = '';
    $('stop-state').textContent = '';
    $('follow-interrupt').checked = false;
    $('detail').hidden = false;
    renderPending();
    await loadDetail();
  }

  async function loadDetail() {
    const id = selected;
    const r = await api('GET', '/api/jobs/' + encodeURIComponent(id));
    if (id !== selected) return;
    if (!r) return;
    $('d-id').textContent = selected;
    if (!r.ok) {
      $('d-status').textContent = 'error: ' + r.error;
      return;
    }
    const j = r.job;
    selectedJob = j;
    $('follow-interrupt').disabled = j.status !== 'running';
    if (j.status !== 'running') $('follow-interrupt').checked = false;
    $('d-status').textContent = j.status;
    $('d-reason').textContent = j.reason_code || '';
    $('d-backend').textContent = j.backend || '';
    $('d-session').textContent = j.session_id || '';
    $('d-parent').textContent = j.parent_job_id || '';
    $('d-cwd').textContent = j.cwd || '';
    $('d-attempts').textContent = String(j.attempts);
    $('d-result').textContent = j.result == null
      ? (j.status === 'queued' || j.status === 'running' ? 'No result yet.' : 'Result unavailable.')
      : j.result === '' ? '(empty result)' : j.result;
    $('stop-job').textContent = j.status === 'queued' || j.status === 'running' ? 'Stop job' : 'Stop agent';
    $('stop-job').disabled = j.status !== 'queued' && j.status !== 'running' && (!j.session_id || j.backend === 'fake');
    await loadLogs();
    if (id !== selected) return;
    const active = j.status === 'running' || j.status === 'queued';
    if (active && !logTimer) logTimer = setInterval(loadDetail, 1500);
    if (!active && logTimer) {
      clearInterval(logTimer);
      logTimer = null;
    }
    renderPending();
  }

  async function loadLogs() {
    if (!selected || logBusy) return;
    const id = selected;
    logBusy = true;
    try {
      for (let page = 0; page < 160; page++) {
        const r = await api('GET', '/api/jobs/' + encodeURIComponent(id) + '/output?offset=' + logOffset);
        if (id !== selected || !r) return;
        if (!r.ok || !r.output) {
          setStatus('logs failed: ' + (r.error || 'missing output'), 'error');
          return;
        }
        const output = r.output;
        if (output.truncated) {
          $('d-logs').textContent += '\n[Earlier log bytes were trimmed]\n';
          logDecoder = new TextDecoder();
        }
        const raw = atob(output.data_base64 || '');
        const bytes = Uint8Array.from(raw, (c) => c.charCodeAt(0));
        $('d-logs').textContent += logDecoder.decode(bytes, { stream: true });
        logOffset = output.next_offset;
        if (logOffset >= output.end_offset) return;
      }
    } finally {
      logBusy = false;
      if (id !== selected) loadLogs();
    }
  }

  function renderPending(message, cls) {
    const mine = pending && pending.jobId === selected;
    $('follow-retry').hidden = !mine;
    $('follow-discard').hidden = !mine;
    $('follow-send').disabled = !!pending || !selectedJob || !selectedJob.session_id;
    const state = $('follow-state');
    state.textContent = message || (mine ? 'Outcome unknown: the follow-up may or may not have been accepted. '
      + 'Check the job list, then retry with the same key or discard.' : '');
    state.className = cls || (mine ? 'warn' : '');
  }

  async function sendFollowUp() {
    const attempt = pending;
    $('follow-send').disabled = true;
    $('follow-retry').disabled = true;
    const r = await api('POST', '/api/jobs/' + encodeURIComponent(attempt.jobId) + '/follow-up',
      { instruction: attempt.text, idempotency_key: attempt.key, interrupt: attempt.interrupt });
    $('follow-retry').disabled = false;
    if (!r) return;
    if (r.lost || r.error === 'outcome_unknown') {
      renderPending();
    } else if (r.ok) {
      pending = null;
      $('follow-text').value = '';
      await loadDetail();
      await loadJobs();
      // After the refresh: loadDetail re-renders the follow-up state and would clear this.
      if (attempt.jobId === selected) renderPending(r.outcome + ': job ' + r.job.job_id, '');
    } else if (r.error === 'daemon_unavailable' || r.error === 'web_busy') {
      // Provably not sent: the same attempt may be retried explicitly.
      renderPending();
      $('follow-state').textContent = 'Not sent (' + r.error + '). Retry with the same key or discard.';
    } else {
      pending = null;
      renderPending('Rejected: ' + r.error, 'error');
    }
  }

  async function stopJob(jobId, status) {
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
    await loadJobs();
    if (selected === jobId) {
      $('stop-state').textContent = message;
      await loadDetail();
    } else {
      setStatus(message, r.ok ? '' : 'error');
    }
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
    $('follow-form').addEventListener('submit', (e) => {
      e.preventDefault();
      const text = $('follow-text').value;
      if (!selected || !selectedJob?.session_id || pending || !text.trim()) return;
      pending = { jobId: selected, key: crypto.randomUUID(), text, interrupt: $('follow-interrupt').checked };
      sendFollowUp();
    });
    $('follow-retry').addEventListener('click', () => { if (pending) sendFollowUp(); });
    $('logs-refresh').addEventListener('click', loadLogs);
    $('stop-job').addEventListener('click', async () => {
      if (!selected || !selectedJob) return;
      await stopJob(selected, selectedJob.status);
    });
    $('follow-discard').addEventListener('click', () => { pending = null; renderPending(); });
  });
})();
