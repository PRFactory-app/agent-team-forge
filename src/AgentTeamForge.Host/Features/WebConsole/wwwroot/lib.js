'use strict';
// Pure helpers (no DOM). parseMarkdown turns agent text into a plain node tree;
// app.js builds DOM nodes from it. Raw HTML, images and non-http(s) links stay literal text.
// Line-based and linear: no regex with nested quantifiers runs over user text.
(() => {
  const MAX_INPUT = 200000;
  const MAX_LINE = 20000;
  const MAX_DEPTH = 3;
  const MAX_CELLS = 20000;
  const MAX_HREF = 2048;
  let cellBudget = MAX_CELLS;
  const HREF = /^https?:\/\/[^\s\u0000-\u001f\u007f]+$/i;

  const text = (v) => ({ t: 'text', v });
  const node = (t, c, extra) => Object.assign({ t, c }, extra);

  function safeHref(raw) {
    const href = raw.trim();
    return href.length <= MAX_HREF && HREF.test(href) ? href : null;
  }

  // indexOf that remembers a failed search, so unmatched openers stay linear.
  function finder(s) {
    const failedFrom = {};
    return (needle, from) => {
      if (failedFrom[needle] !== undefined && from >= failedFrom[needle]) return -1;
      const at = s.indexOf(needle, from);
      if (at < 0) failedFrom[needle] = Math.min(failedFrom[needle] ?? from, from);
      return at;
    };
  }

  const isWord = (ch) => ch !== undefined && ch !== '' && /[A-Za-z0-9]/.test(ch);

  // Matches `[label](url)` at s[i] === '['; returns { end, label, url } or null.
  function matchLink(s, i, find) {
    const close = find(']', i + 1);
    if (close < 0 || s[close + 1] !== '(') return null;
    const end = find(')', close + 2);
    if (end < 0) return null;
    return { end: end + 1, label: s.slice(i + 1, close), url: s.slice(close + 2, end) };
  }

  function parseInline(s, depth = 0) {
    const out = [];
    let buf = '';
    const flush = () => { if (buf) { out.push(text(buf)); buf = ''; } };
    if (s.length > MAX_LINE) return [text(s)];
    const find = finder(s);
    let i = 0;
    while (i < s.length) {
      const ch = s[i];
      if (ch === '`') {
        const end = find('`', i + 1);
        if (end > i + 1) {
          flush();
          out.push({ t: 'code', v: s.slice(i + 1, end) });
          i = end + 1;
          continue;
        }
      } else if (ch === '!' && s[i + 1] === '[') {
        const m = matchLink(s, i + 1, find);
        if (m) { buf += s.slice(i, m.end); i = m.end; continue; }
      } else if (ch === '[' && depth < MAX_DEPTH) {
        const m = matchLink(s, i, find);
        const href = m && m.label ? safeHref(m.url) : null;
        if (href) {
          flush();
          out.push(node('a', parseInline(m.label, depth + 1), { href }));
          i = m.end;
          continue;
        }
      } else if (ch === '*' && s[i + 1] === '*' && depth < MAX_DEPTH) {
        const end = find('**', i + 2);
        if (end > i + 2) {
          flush();
          out.push(node('strong', parseInline(s.slice(i + 2, end), depth + 1)));
          i = end + 2;
          continue;
        }
      } else if ((ch === '*' || ch === '_') && depth < MAX_DEPTH && s[i + 1] !== ' '
        && !(ch === '_' && isWord(s[i - 1]))) {
        const end = find(ch, i + 1);
        if (end > i + 1 && s[end - 1] !== ' ' && !(ch === '_' && isWord(s[end + 1])) && s[end + 1] !== ch) {
          flush();
          out.push(node('em', parseInline(s.slice(i + 1, end), depth + 1)));
          i = end + 1;
          continue;
        }
      } else if ((ch === 'h' || ch === 'H') && !isWord(s[i - 1]) && s[i - 1] !== '(') {
        const head = s.slice(i, i + 8).toLowerCase();
        if (head.startsWith('http://') || head.startsWith('https://')) {
          // Scan at most one href's worth; a rejected or over-long token is consumed as literal
          // text so its suffix is never rescanned.
          const limit = Math.min(s.length, i + MAX_HREF + 2);
          let end = i;
          while (end < limit && !/[\s<>]/.test(s[end])) end++;
          if (end < s.length && end === limit) {
            while (end < s.length && !/[\s<>]/.test(s[end])) end++;
            buf += s.slice(i, end);
            i = end;
            continue;
          }
          while (end > i && '.,;:!?)]}\'"*_'.includes(s[end - 1])) end--;
          const url = s.slice(i, end);
          const href = safeHref(url);
          if (href) {
            flush();
            out.push(node('a', [text(url)], { href }));
          } else {
            buf += url;
          }
          i = end;
          continue;
        }
      }
      buf += ch;
      i++;
    }
    flush();
    return out;
  }

  const FENCE = /^ {0,3}(`{3,}|~{3,})/;
  const HEADING = /^ {0,3}(#{1,6})[ \t]+(.*)$/;
  const ITEM = /^( *)([-*+]|\d{1,9}[.)]) +(.*)$/;
  const SEPARATOR_CELL = /^:?-+:?$/;

  function splitRow(line) {
    let body = line.trim();
    if (body.startsWith('|')) body = body.slice(1);
    if (body.endsWith('|') && !body.endsWith('\\|')) body = body.slice(0, -1);
    const cells = [];
    let cell = '';
    for (let i = 0; i < body.length; i++) {
      if (body[i] === '\\' && body[i + 1] === '|') { cell += '|'; i++; }
      else if (body[i] === '|') { cells.push(cell.trim()); cell = ''; }
      else cell += body[i];
    }
    cells.push(cell.trim());
    return cells;
  }

  function isSeparator(line) {
    if (line === undefined || !line.includes('-')) return false;
    const cells = splitRow(line);
    return cells.length > 0 && cells.every(c => SEPARATOR_CELL.test(c));
  }

  const isTableStart = (lines, i) => lines[i].includes('|') && isSeparator(lines[i + 1])
    && splitRow(lines[i]).length === splitRow(lines[i + 1]).length;

  const blank = (line) => line.trim() === '';

  function startsBlock(lines, i) {
    const line = lines[i];
    return FENCE.test(line) || HEADING.test(line) || ITEM.test(line) || isTableStart(lines, i);
  }

  function parseList(lines, start) {
    const first = ITEM.exec(lines[start]);
    const baseIndent = first[1].length;
    const root = node(/^\d/.test(first[2]) ? 'ol' : 'ul', []);
    let i = start;
    let nested = null;
    while (i < lines.length) {
      const m = ITEM.exec(lines[i]);
      if (!m || m[1].length < baseIndent) break;
      const ordered = /^\d/.test(m[2]);
      if (m[1].length - baseIndent >= 2) {
        const last = root.c[root.c.length - 1];
        if (!nested || nested.t !== (ordered ? 'ol' : 'ul')) {
          nested = node(ordered ? 'ol' : 'ul', []);
          last.c.push(nested);
        }
        nested.c.push(node('li', parseInline(m[3])));
      } else {
        nested = null;
        root.c.push(node('li', parseInline(m[3])));
      }
      i++;
    }
    return { block: root, next: i };
  }

  // "Title ##" -> "Title" without a backtracking regex.
  function stripClosingHashes(s) {
    let end = s.length;
    while (end > 0 && (s[end - 1] === ' ' || s[end - 1] === '\t')) end--;
    let hashes = end;
    while (hashes > 0 && s[hashes - 1] === '#') hashes--;
    return hashes < end && hashes > 0 && (s[hashes - 1] === ' ' || s[hashes - 1] === '\t') ? s.slice(0, hashes).trimEnd() : s;
  }

  function parseBlocks(lines, raw) {
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (blank(line)) { i++; continue; }
      const fence = FENCE.exec(line);
      if (fence) {
        const marker = fence[1];
        const body = [];
        i++;
        while (i < lines.length) {
          const t = lines[i].trim();
          if (t.length >= marker.length && t[0] === marker[0] && t.split(t[0]).join('') === '') break;
          body.push(raw[i]);
          i++;
        }
        i++;
        out.push({ t: 'pre', v: body.join('\n') });
        continue;
      }
      const heading = HEADING.exec(line);
      if (heading) {
        out.push(node('h', parseInline(stripClosingHashes(heading[2])), { level: heading[1].length }));
        i++;
        continue;
      }
      if (isTableStart(lines, i)) {
        const head = splitRow(lines[i]);
        const first = i;
        i += 2;
        let last = i;
        while (last < lines.length && !blank(lines[last]) && lines[last].includes('|')) last++;
        // Rows are padded to the header width, so budget the generated cells before allocating any.
        const cost = head.length * (last - i + 1);
        if (cost > cellBudget) {
          out.push({ t: 'pre', v: raw.slice(first, last).join('\n') });
          i = last;
          continue;
        }
        cellBudget -= cost;
        const rows = [node('tr', head.map(c => node('th', parseInline(c))))];
        for (; i < last; i++) {
          const cells = splitRow(lines[i]);
          while (cells.length < head.length) cells.push('');
          rows.push(node('tr', cells.slice(0, head.length).map(c => node('td', parseInline(c)))));
        }
        out.push(node('table', rows));
        continue;
      }
      if (ITEM.test(line)) {
        const { block, next } = parseList(lines, i);
        out.push(block);
        i = next;
        continue;
      }
      const para = [];
      while (i < lines.length && !blank(lines[i]) && (para.length === 0 || !startsBlock(lines, i))) {
        para.push(lines[i]);
        i++;
      }
      const c = [];
      para.forEach((p, n) => {
        if (n > 0) c.push({ t: 'br' });
        c.push(...parseInline(p.trim()));
      });
      out.push(node('p', c));
    }
    return out;
  }

  // Tabs only matter as indentation for list markers; fenced code keeps the raw lines.
  function expandLeadingTabs(line) {
    let end = 0;
    while (end < line.length && (line[end] === ' ' || line[end] === '\t')) end++;
    return line.indexOf('\t') < 0 || line.indexOf('\t') >= end ? line
      : line.slice(0, end).split('\t').join('  ') + line.slice(end);
  }

  function parseMarkdown(input) {
    const s = String(input ?? '');
    if (s.length > MAX_INPUT) return [{ t: 'pre', v: s }];
    cellBudget = MAX_CELLS;
    const raw = s.replace(/\r\n?/g, '\n').split('\n');
    return parseBlocks(raw.map(expandLeadingTabs), raw);
  }

  globalThis.AtfLib = { parseMarkdown, safeHref };
})();
