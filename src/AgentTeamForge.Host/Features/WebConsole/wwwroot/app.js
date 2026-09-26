'use strict';
// Text-only operator console. All server text is rendered with textContent.
// The bearer lives only in this closure: never in the URL, storage or cookies.
(() => {
  const $ = (id) => document.getElementById(id);
  let token = null;
  let selected = null;
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
    const r = await api('GET', '/api/jobs');
    if (!r) return;
    if (!r.ok) {
      setStatus('list failed: ' + r.error, 'error');
      return;
    }
    setStatus('updated ' + new Date().toLocaleTimeString());
    const tbody = $('jobs');
    tbody.replaceChildren();
    for (const j of (r.page && r.page.jobs) || r.jobs || []) {
      const tr = document.createElement('tr');
      if (j.job_id === selected) tr.className = 'selected';
      const idCell = document.createElement('td');
      const link = document.createElement('button');
      link.type = 'button';
      link.textContent = j.job_id;
      link.addEventListener('click', () => select(j.job_id));
      idCell.append(link);
      tr.append(idCell, cell(j.status), cell(j.backend), cell(j.session_id), cell(j.parent_job_id), cell(j.attempts));
      tbody.append(tr);
    }
    if (selected) await loadDetail();
  }

  async function select(jobId) {
    selected = jobId;
    $('detail').hidden = false;
    renderPending();
    await loadJobs();
  }

  async function loadDetail() {
    const r = await api('GET', '/api/jobs/' + encodeURIComponent(selected));
    if (!r) return;
    $('d-id').textContent = selected;
    if (!r.ok) {
      $('d-status').textContent = 'error: ' + r.error;
      return;
    }
    const j = r.job;
    $('d-status').textContent = j.status;
    $('d-reason').textContent = j.reason_code || '';
    $('d-backend').textContent = j.backend || '';
    $('d-session').textContent = j.session_id || '';
    $('d-parent').textContent = j.parent_job_id || '';
    $('d-cwd').textContent = j.cwd || '';
    $('d-attempts').textContent = String(j.attempts);
    $('d-result').textContent = j.result || '';
    await loadLogs();
  }

  // TODO(codex): keep next_offset and append instead of reloading the head.
  async function loadLogs() {
    if (!selected) return;
    const r = await api('GET', '/api/jobs/' + encodeURIComponent(selected) + '/output?offset=0');
    if (!r) return;
    $('d-logs').textContent = r.ok ? ((r.output && r.output.text) || '') : 'error: ' + r.error;
  }

  function renderPending(message, cls) {
    const mine = pending && pending.jobId === selected;
    $('follow-retry').hidden = !mine;
    $('follow-discard').hidden = !mine;
    $('follow-send').disabled = !!pending;
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
      { instruction: attempt.text, idempotency_key: attempt.key });
    $('follow-retry').disabled = false;
    if (!r) return;
    if (r.lost || r.error === 'outcome_unknown') {
      renderPending();
    } else if (r.ok) {
      pending = null;
      $('follow-text').value = '';
      renderPending(r.outcome + ': job ' + r.job.job_id, '');
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
    $('follow-form').addEventListener('submit', (e) => {
      e.preventDefault();
      const text = $('follow-text').value;
      if (!selected || pending || !text.trim()) return;
      pending = { jobId: selected, key: crypto.randomUUID(), text };
      sendFollowUp();
    });
    $('follow-retry').addEventListener('click', () => { if (pending) sendFollowUp(); });
    $('logs-refresh').addEventListener('click', loadLogs);
    $('stop-job').addEventListener('click', async () => {
      if (!selected) return;
      const r = await api('POST', '/api/jobs/' + encodeURIComponent(selected) + '/stop');
      if (!r) return;
      $('stop-state').textContent = r.ok ? 'stop: ' + (r.job ? r.job.status : 'ok') : 'stop failed: ' + r.error;
      await loadJobs();
    });
    $('follow-discard').addEventListener('click', () => { pending = null; renderPending(); });
  });
})();
