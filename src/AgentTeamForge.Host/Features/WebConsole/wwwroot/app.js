'use strict';
// Text-only operator console. All server text is rendered with textContent.
// The bearer lives only in this closure: never in the URL, storage or cookies.
(() => {
  const $ = (id) => document.getElementById(id);
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
    clearInterval(timer);
    clearInterval(logTimer);
    $('console').hidden = true;
    $('login').hidden = false;
    setStatus(message, 'error');
  }

  function cell(text) {
    const td = document.createElement('td');
    td.textContent = text == null ? '' : String(text);
    return td;
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
    const tbody = $('jobs');
    tbody.replaceChildren();
    nextCursor = r.page && r.page.has_more ? r.page.next_cursor : null;
    $('page-prev').disabled = pageIndex === 0;
    $('page-next').disabled = !nextCursor;
    $('page-number').textContent = 'Page ' + (pageIndex + 1);
    for (const j of (r.page && r.page.jobs) || []) {
      const tr = document.createElement('tr');
      if (j.job_id === selected) tr.className = 'selected';
      const idCell = document.createElement('td');
      const link = document.createElement('button');
      link.type = 'button';
      link.textContent = j.job_id;
      link.addEventListener('click', () => select(j.job_id));
      idCell.append(link);
      tr.append(idCell, cell(j.status), cell(j.backend), cell(j.session_id), cell(j.parent_job_id), cell(j.attempts));
      const action = document.createElement('td');
      if (j.session_id) {
        const follow = document.createElement('button');
        follow.type = 'button';
        follow.textContent = 'Follow up';
        follow.addEventListener('click', async () => {
          await select(j.job_id);
          $('follow-text').focus();
        });
        action.append(follow);
      }
      tr.append(action);
      tbody.append(tr);
    }
  }

  async function select(jobId) {
    clearInterval(logTimer);
    logTimer = null;
    selected = jobId;
    selectedJob = null;
    logOffset = 0;
    logDecoder = new TextDecoder();
    $('d-logs').textContent = '';
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
    $('d-result').textContent = j.result || '';
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
      renderPending(r.outcome + ': job ' + r.job.job_id, '');
      await loadDetail();
      await loadJobs();
    } else if (r.error === 'daemon_unavailable' || r.error === 'web_busy') {
      // Provably not sent: the same attempt may be retried explicitly.
      renderPending();
      $('follow-state').textContent = 'Not sent (' + r.error + '). Retry with the same key or discard.';
    } else {
      pending = null;
      renderPending('Rejected: ' + r.error, 'error');
    }
  }

  document.addEventListener('DOMContentLoaded', () => {
    $('login-form').addEventListener('submit', (e) => {
      e.preventDefault();
      token = $('token').value.trim();
      $('token').value = '';
      $('login').hidden = true;
      $('console').hidden = false;
      setStatus('connecting');
      loadJobs();
      timer = setInterval(loadJobs, 5000);
    });
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
      if (!selected || !window.confirm('Stop job ' + selected + '?')) return;
      const r = await api('POST', '/api/jobs/' + encodeURIComponent(selected) + '/stop');
      if (!r) return;
      $('stop-state').textContent = r.lost || r.error === 'outcome_unknown'
        ? 'Stop outcome unknown; check the job status before trying again.'
        : r.ok
        ? 'Outcome: ' + (r.outcome || 'unknown') + '; status: ' + (r.job?.status || 'unknown')
          + '; reason_code: ' + (r.job?.reason_code || 'none')
        : 'Stop failed: ' + r.error;
      await loadDetail();
      await loadJobs();
    });
    $('follow-discard').addEventListener('click', () => { pending = null; renderPending(); });
  });
})();
