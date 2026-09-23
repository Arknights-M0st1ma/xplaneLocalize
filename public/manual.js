// Manual reader: the PDFs live on the PC, this page streams and renders them.
//
// Everything the reader needs (outline/bookmarks, thumbnails, continuous
// scrolling with lazy rendering, zoom, rotate, in-document search, remembered
// position) is built on the vendored PDF.js in /vendor/pdfjs.

import * as pdfjs from '/vendor/pdfjs/pdf.min.mjs';

pdfjs.GlobalWorkerOptions.workerSrc = '/vendor/pdfjs/pdf.worker.min.mjs';

const $ = (selector) => document.querySelector(selector);
const CMAP_URL = '/vendor/pdfjs/cmaps/';
const STANDARD_FONTS_URL = '/vendor/pdfjs/standard_fonts/';
const LAST_DOCUMENT_KEY = 'efb.pdf.last';
const POSITION_PREFIX = 'efb.pdf.pos.';
const MIN_SCALE = 0.25;
const MAX_SCALE = 6;
const THUMB_WIDTH = 132;

const read = (key) => { try { return localStorage.getItem(key) ?? ''; } catch { return ''; } };
const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private mode */ } };

const state = {
  listing: { configured: false, folder: '', files: [] },
  filter: '',
  doc: null,
  url: '',
  path: '',
  name: '',
  pages: [],          // { number, width, height, holder, canvas, textLayer, rendered, rendering }
  pageCount: 0,
  scale: 1,
  zoomMode: 'fit-width',
  rotation: 0,
  current: 1,
  hits: [],
  hitIndex: -1,
  searchToken: 0,
  scrollSync: false
};

// --------------------------------------------------------------- file listing
function formatSize(bytes) {
  if (!Number.isFinite(bytes)) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function renderList() {
  const list = $('#manualList');
  const status = $('#manualStatus');
  if (!list || !status) return;
  const listing = state.listing;
  list.innerHTML = '';
  if (!listing.configured) {
    status.textContent = listing.error
      ? `${listing.error}：请在电脑端“设置… → 地图 → 手册文件夹”里指定一个文件夹。`
      : '尚未配置手册文件夹。请在电脑端“设置… → 地图 → 手册文件夹”里指定一个文件夹，PDF 会出现在这里。';
    return;
  }
  const needle = state.filter.trim().toLowerCase();
  const files = needle
    ? listing.files.filter((file) => file.path.toLowerCase().includes(needle))
    : listing.files;
  status.textContent = listing.files.length === 0
    ? `文件夹里没有 PDF${listing.folder ? `（${listing.folder}）` : ''}`
    : `共 ${listing.files.length} 本${needle ? `，匹配 ${files.length} 本` : ''}${listing.truncated ? '（只列出前 600 本）' : ''}`;
  for (const file of files) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'manual-item';
    button.setAttribute('role', 'listitem');
    if (file.path === state.path) button.classList.add('active');
    const folder = file.path.includes('/') ? file.path.slice(0, file.path.lastIndexOf('/')) : '';
    button.innerHTML = `
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4.5A2.5 2.5 0 0 1 6.5 2H20v18H6.5A2.5 2.5 0 0 0 4 22V4.5Z"/></svg>
      <span class="manual-item-text"><b></b><i></i></span>`;
    button.querySelector('b').textContent = file.name;
    button.querySelector('i').textContent = [folder, formatSize(file.size)].filter(Boolean).join(' · ');
    button.addEventListener('click', () => openManual(file.path));
    list.appendChild(button);
  }
}

export function manualInfo() {
  return {
    configured: state.listing.configured,
    folder: state.listing.folder,
    count: state.listing.files.length,
    error: state.listing.error ?? '',
    opened: state.name
  };
}

export async function refreshManuals({ reopen = true } = {}) {
  const status = $('#manualStatus');
  if (status) status.textContent = '正在扫描文件夹…';
  try {
    const response = await fetch('/api/manuals');
    state.listing = await response.json();
  } catch (error) {
    state.listing = { configured: false, folder: '', files: [], error: `无法读取手册列表：${error.message}` };
  }
  renderList();
  const first = state.listing.files[0];
  const remembered = state.listing.files.find((file) => file.path === read(LAST_DOCUMENT_KEY));
  const wanted = remembered || first;
  if (reopen && wanted && (state.path === '' || !state.listing.files.some((file) => file.path === state.path))) {
    openManual(wanted.path);
  }
}

// ------------------------------------------------------------------ rendering
function viewportFor(width, height) {
  // The page is laid out in CSS pixels: natural size × scale, swapped when the
  // document is rotated by 90/270 degrees.
  const rotated = state.rotation % 180 !== 0;
  const w = (rotated ? height : width) * state.scale;
  const h = (rotated ? width : height) * state.scale;
  return { width: Math.max(1, Math.round(w)), height: Math.max(1, Math.round(h)) };
}

function layoutPages() {
  for (const entry of state.pages) {
    const { width, height } = viewportFor(entry.width, entry.height);
    entry.holder.style.width = `${width}px`;
    entry.holder.style.height = `${height}px`;
    // The old bitmap is at the wrong size now: drop it and let the observer
    // render this page again (the spinner comes back with it).
    entry.holder.removeAttribute('data-rendered');
    entry.rendered = false;
    entry.rendering = false;
    if (entry.canvas) entry.canvas.style.display = 'none';
    if (entry.textLayer) entry.textLayer.style.display = 'none';
  }
  renderVisible();
}

function fitScale() {
  const scroll = $('#pdfScroll');
  const first = state.pages[0];
  if (!scroll || !first) return 1;
  const rotated = state.rotation % 180 !== 0;
  const naturalWidth = rotated ? first.height : first.width;
  const naturalHeight = rotated ? first.width : first.height;
  const available = scroll.clientWidth - 32;
  const availableHeight = scroll.clientHeight - 32;
  if (state.zoomMode === 'fit-page') {
    return Math.min(available / naturalWidth, availableHeight / naturalHeight);
  }
  return available / naturalWidth;
}

function applyZoomMode() {
  if (state.zoomMode === 'fit-width' || state.zoomMode === 'fit-page') {
    state.scale = clampScale(fitScale());
  }
  $('#pdfZoomLabel').textContent = `${Math.round(state.scale * 100)}%`;
  layoutPages();
}

const clampScale = (value) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, value));

function setScale(next) {
  state.zoomMode = 'custom';
  state.scale = clampScale(next);
  $('#pdfZoomLabel').textContent = `${Math.round(state.scale * 100)}%`;
  layoutPages();
}

async function renderPage(entry) {
  if (!state.doc || entry.rendering || entry.rendered) return;
  entry.rendering = true;
  try {
    const page = await state.doc.getPage(entry.number);
    const viewport = page.getViewport({ scale: state.scale, rotation: state.rotation });
    const ratio = Math.min(2, window.devicePixelRatio || 1);
    const canvas = entry.canvas ?? document.createElement('canvas');
    canvas.className = 'pdf-canvas';
    canvas.width = Math.floor(viewport.width * ratio);
    canvas.height = Math.floor(viewport.height * ratio);
    canvas.style.width = `${Math.round(viewport.width)}px`;
    canvas.style.height = `${Math.round(viewport.height)}px`;
    if (!entry.canvas) {
      entry.holder.appendChild(canvas);
      entry.canvas = canvas;
    }
    canvas.style.display = '';
    const context = canvas.getContext('2d', { alpha: false });
    await page.render({
      canvasContext: context,
      viewport,
      transform: ratio === 1 ? null : [ratio, 0, 0, ratio, 0, 0]
    }).promise;
    // Selectable text on top of the canvas, which also gives us search hits.
    if (typeof pdfjs.TextLayer === 'function') {
      let layer = entry.textLayer;
      if (!layer) {
        layer = document.createElement('div');
        layer.className = 'textLayer';
        entry.holder.appendChild(layer);
        entry.textLayer = layer;
      }
      layer.innerHTML = '';
      layer.style.display = '';
      const textContent = await page.getTextContent();
      const textLayer = new pdfjs.TextLayer({ textContentSource: textContent, container: layer, viewport });
      await textLayer.render();
      entry.textContent = textContent;
      markHits(entry);
    }
    entry.rendered = true;
    entry.holder.setAttribute('data-rendered', '');
  } catch (error) {
    if (error?.name !== 'RenderingCancelledException') console.warn('PDF 渲染失败', entry.number, error);
  } finally {
    entry.rendering = false;
  }
}

function markHits(entry) {
  if (!entry.textLayer || state.hits.length === 0) return;
  const items = new Set(state.hits.filter((hit) => hit.page === entry.number).map((hit) => hit.item));
  const spans = entry.textLayer.querySelectorAll('span');
  items.forEach((item) => spans[item]?.classList.add('highlight'));
}

let observer;
function renderVisible() {
  const scroll = $('#pdfScroll');
  if (!observer && 'IntersectionObserver' in window) {
    observer = new IntersectionObserver((entries) => {
      for (const item of entries) {
        const entry = state.pages.find((page) => page.holder === item.target);
        if (entry && item.isIntersecting) renderPage(entry);
      }
    }, { root: scroll, rootMargin: '600px 0px' });
  }
  if (!observer) {
    state.pages.forEach(renderPage);
    return;
  }
  observer.disconnect();
  for (const entry of state.pages) {
    if (!entry.rendered) observer.observe(entry.holder);
  }
}

function updateCurrentPage() {
  const scroll = $('#pdfScroll');
  if (!scroll) return;
  const top = scroll.scrollTop + 8;
  let current = state.current;
  for (const entry of state.pages) {
    if (entry.holder.offsetTop <= top) current = entry.number;
    else break;
  }
  state.current = current;
  const input = $('#pdfPageInput');
  if (input && document.activeElement !== input) input.value = String(current);
}

function gotoPage(number, { smooth = false } = {}) {
  const entry = state.pages[Math.min(state.pages.length, Math.max(1, number)) - 1];
  if (!entry) return;
  const scroll = $('#pdfScroll');
  scroll.scrollTo({ top: entry.holder.offsetTop, behavior: smooth ? 'smooth' : 'auto' });
  state.current = entry.number;
  const input = $('#pdfPageInput');
  if (input) input.value = String(entry.number);
  renderPage(entry);
}

// ------------------------------------------------------------------- outline
async function renderOutline() {
  const container = $('#pdfOutline');
  container.innerHTML = '';
  let outline = null;
  try {
    outline = await state.doc.getOutline();
  } catch { /* a document without an outline is normal */ }
  if (!outline || outline.length === 0) {
    container.innerHTML = '<p class="section-note">这本 PDF 没有书签/大纲。</p>';
    return;
  }
  const build = (items, depth) => {
    const list = document.createElement('div');
    list.className = 'pdf-outline-level';
    for (const item of items) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'pdf-outline-item';
      button.style.paddingLeft = `${6 + depth * 14}px`;
      button.textContent = item.title || '(未命名书签)';
      button.addEventListener('click', async () => {
        try {
          const destination = typeof item.dest === 'string' ? await state.doc.getDestination(item.dest) : item.dest;
          if (!destination) return;
          const index = await state.doc.getPageIndex(destination[0]);
          gotoPage(index + 1, { smooth: true });
        } catch { /* a broken bookmark must not break the reader */ }
      });
      list.appendChild(button);
      if (item.items?.length) list.appendChild(build(item.items, depth + 1));
    }
    return list;
  };
  container.appendChild(build(outline, 0));
}

let thumbObserver = null;

async function renderThumbnails() {
  const container = $('#pdfThumbs');
  container.innerHTML = '';
  thumbObserver?.disconnect();
  thumbObserver = null;
  for (const entry of state.pages) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'pdf-thumb';
    button.dataset.page = String(entry.number);
    button.innerHTML = '<span class="pdf-thumb-box"></span><i></i>';
    button.querySelector('i').textContent = String(entry.number);
    button.addEventListener('click', () => gotoPage(entry.number, { smooth: true }));
    container.appendChild(button);
  }
}

/**
 * Thumbnails are drawn lazily, but only once the panel is actually on screen:
 * an IntersectionObserver rooted in a display:none container never fires, which
 * is what left the panel blank.
 */
function observeThumbnails() {
  const container = $('#pdfThumbs');
  const thumbs = [...container.querySelectorAll('.pdf-thumb')];
  if (thumbs.length === 0) return;
  if (!('IntersectionObserver' in window)) {
    for (const thumb of thumbs) drawThumb(thumb);
    return;
  }
  if (thumbObserver) return;
  thumbObserver = new IntersectionObserver((items) => {
    for (const item of items) {
      if (!item.isIntersecting) continue;
      thumbObserver.unobserve(item.target);
      drawThumb(item.target);
    }
  }, { rootMargin: '400px 0px' });
  for (const thumb of thumbs) thumbObserver.observe(thumb);
}

async function drawThumb(button) {
  if (button.dataset.drawn === '1') return;
  button.dataset.drawn = '1';
  const number = Number(button.dataset.page);
  const box = button.querySelector('.pdf-thumb-box');
  try {
    const page = await state.doc.getPage(number);
    const base = page.getViewport({ scale: 1 });
    const scale = THUMB_WIDTH / base.width;
    const viewport = page.getViewport({ scale });
    const canvas = document.createElement('canvas');
    canvas.width = Math.floor(viewport.width);
    canvas.height = Math.floor(viewport.height);
    box.appendChild(canvas);
    await page.render({ canvasContext: canvas.getContext('2d', { alpha: false }), viewport }).promise;
  } catch { /* leave the placeholder */ }
}

// -------------------------------------------------------------------- search
function setSearchStatus(text) {
  const node = $('#pdfSearchStatus');
  if (node) node.textContent = text;
}

async function runSearch(query) {
  const token = ++state.searchToken;
  state.hits = [];
  state.hitIndex = -1;
  if (query.length < 2) {
    setSearchStatus(query.length === 0 ? '' : '至少输入 2 个字符');
    for (const entry of state.pages) markHits(entry);
    return;
  }
  const needle = query.toLowerCase();
  setSearchStatus('搜索中…');
  for (const entry of state.pages) {
    if (token !== state.searchToken) return;
    try {
      const textContent = entry.textContent ?? (await (await state.doc.getPage(entry.number)).getTextContent());
      entry.textContent = textContent;
      textContent.items.forEach((item, index) => {
        if (typeof item.str === 'string' && item.str.toLowerCase().includes(needle)) {
          state.hits.push({ page: entry.number, item: index });
        }
      });
    } catch { /* skip a page we cannot read */ }
    setSearchStatus(`搜索中… ${entry.number}/${state.pageCount}`);
    // Yield so a long search never freezes the page.
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  if (token !== state.searchToken) return;
  setSearchStatus(state.hits.length === 0 ? '没有匹配' : `${state.hits.length} 处匹配`);
  for (const entry of state.pages) markHits(entry);
  if (state.hits.length > 0) stepHit(1, { fresh: true });
}

function stepHit(direction, { fresh = false } = {}) {
  if (state.hits.length === 0) return;
  state.hitIndex = fresh
    ? 0
    : (state.hitIndex + direction + state.hits.length) % state.hits.length;
  const hit = state.hits[state.hitIndex];
  setSearchStatus(`${state.hitIndex + 1}/${state.hits.length} 处匹配 · 第 ${hit.page} 页`);
  gotoPage(hit.page, { smooth: true });
}

// --------------------------------------------------------------------- open
export async function openManual(relativePath) {
  const empty = $('#pdfEmpty');
  const scroll = $('#pdfScroll');
  state.searchToken += 1;
  state.hits = [];
  state.hitIndex = -1;
  setSearchStatus('');
  try {
    if (state.doc) await state.doc.destroy();
  } catch { /* ignore */ }
  state.doc = null;
  state.pages = [];
  $('#pdfPages').innerHTML = '';
  $('#pdfOutline').innerHTML = '';
  $('#pdfThumbs').innerHTML = '';
  if (observer) { observer.disconnect(); observer = null; }
  state.path = relativePath;
  state.name = relativePath.split('/').pop() ?? relativePath;
  state.rotation = 0;
  state.current = 1;
  write(LAST_DOCUMENT_KEY, relativePath);
  renderList();
  if (empty) empty.classList.add('hidden');
  if (scroll) scroll.classList.remove('hidden');
  setSearchStatus('载入中…');
  state.url = `/api/manuals/file?path=${encodeURIComponent(relativePath)}`;
  try {
    state.doc = await pdfjs.getDocument({
      url: state.url,
      cMapUrl: CMAP_URL,
      cMapPacked: true,
      standardFontDataUrl: STANDARD_FONTS_URL
    }).promise;
  } catch (error) {
    setSearchStatus('');
    if (empty) {
      empty.classList.remove('hidden');
      empty.innerHTML = `<h2>打不开这本手册</h2><p>${state.name}：${error.message}</p>`;
    }
    if (scroll) scroll.classList.add('hidden');
    return;
  }
  state.pageCount = state.doc.numPages;
  $('#pdfPageCount').textContent = String(state.pageCount);
  $('#pdfZoomLabel').textContent = '–';
  const container = $('#pdfPages');
  for (let number = 1; number <= state.pageCount; number += 1) {
    const page = await state.doc.getPage(number);
    const base = page.getViewport({ scale: 1 });
    const holder = document.createElement('div');
    holder.className = 'pdf-page';
    holder.dataset.page = String(number);
    holder.innerHTML = '<span class="pdf-spinner"></span>';
    container.appendChild(holder);
    state.pages.push({
      number, width: base.width, height: base.height, holder,
      canvas: null, textLayer: null, rendered: false, rendering: false, textContent: null
    });
  }
  // Fit to the container before the first scroll so the first paint is right.
  state.zoomMode = state.zoomMode === 'custom' ? 'fit-width' : state.zoomMode;
  applyZoomMode();
  renderOutline();
  renderThumbnails();
  const remembered = Number(read(POSITION_PREFIX + relativePath)) || 1;
  state.scrollSync = false;
  gotoPage(Math.min(state.pageCount, Math.max(1, remembered)));
  setTimeout(() => { state.scrollSync = true; }, 400);
  updateSideVisibility();
}

function updateSideVisibility() {
  const side = $('#pdfSide');
  const outline = $('#pdfOutline');
  const thumbs = $('#pdfThumbs');
  const wantOutline = $('#pdfOutlineToggle').getAttribute('aria-pressed') === 'true';
  const wantThumbs = $('#pdfThumbToggle').getAttribute('aria-pressed') === 'true';
  // toggle(), not add(): adding only ever hid the panel, so switching from
  // thumbnails to the outline (or back) left an empty side panel behind.
  outline.classList.toggle('hidden', !wantOutline);
  thumbs.classList.toggle('hidden', !wantThumbs);
  side.classList.toggle('hidden', !(wantOutline || wantThumbs));
  if (wantThumbs) observeThumbnails();
}

function throttle(fn, ms) {
  let last = 0;
  let timer = 0;
  return (...args) => {
    const now = Date.now();
    if (now - last >= ms) {
      last = now;
      fn(...args);
    } else if (!timer) {
      timer = setTimeout(() => { timer = 0; last = Date.now(); fn(...args); }, ms - (now - last));
    }
  };
}

export function initManual() {
  const scroll = $('#pdfScroll');
  scroll?.addEventListener('scroll', throttle(() => {
    updateCurrentPage();
    if (state.scrollSync && state.path) write(POSITION_PREFIX + state.path, String(state.current));
  }, 150), { passive: true });

  $('#pdfPrev')?.addEventListener('click', () => gotoPage(state.current - 1, { smooth: true }));
  $('#pdfNext')?.addEventListener('click', () => gotoPage(state.current + 1, { smooth: true }));
  $('#pdfPageInput')?.addEventListener('change', (event) => {
    const value = Number.parseInt(event.target.value, 10);
    if (Number.isFinite(value)) gotoPage(value);
    else event.target.value = String(state.current);
  });
  $('#pdfZoomIn')?.addEventListener('click', () => setScale(state.scale * 1.2));
  $('#pdfZoomOut')?.addEventListener('click', () => setScale(state.scale / 1.2));
  $('#pdfFitWidth')?.addEventListener('click', () => { state.zoomMode = 'fit-width'; applyZoomMode(); });
  $('#pdfFitPage')?.addEventListener('click', () => { state.zoomMode = 'fit-page'; applyZoomMode(); });
  $('#pdfRotate')?.addEventListener('click', () => {
    state.rotation = (state.rotation + 90) % 360;
    for (const entry of state.pages) {
      entry.rendered = false;
      entry.rendering = false;
    }
    applyZoomMode();
  });
  for (const [id, which] of [['#pdfOutlineToggle', 'outline'], ['#pdfThumbToggle', 'thumbs']]) {
    $(id)?.addEventListener('click', (event) => {
      const pressed = event.currentTarget.getAttribute('aria-pressed') === 'true';
      event.currentTarget.setAttribute('aria-pressed', String(!pressed));
      event.currentTarget.classList.toggle('active', !pressed);
      if (!pressed) {
        // Only one side panel at a time keeps the reader usable on an iPad.
        const other = which === 'outline' ? '#pdfThumbToggle' : '#pdfOutlineToggle';
        $(other)?.setAttribute('aria-pressed', 'false');
        $(other)?.classList.remove('active');
      }
      updateSideVisibility();
    });
  }
  $('#pdfDownload')?.addEventListener('click', () => {
    if (!state.url) return;
    const link = document.createElement('a');
    link.href = state.url;
    link.download = state.name;
    document.body.appendChild(link);
    link.click();
    link.remove();
  });
  const search = $('#pdfSearch');
  search?.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    if (state.hits.length > 0) stepHit(event.shiftKey ? -1 : 1);
    else runSearch(search.value.trim());
  });
  // debounce() forwards the event object, so read the value here rather than in
  // the debounced callback (which used to receive an Event and throw on .trim()).
  search?.addEventListener('input', debounce(() => runSearch(search.value.trim()), 500));
  $('#pdfSearchNext')?.addEventListener('click', () => (state.hits.length > 0 ? stepHit(1) : runSearch(search.value.trim())));
  $('#pdfSearchPrev')?.addEventListener('click', () => (state.hits.length > 0 ? stepHit(-1) : runSearch(search.value.trim())));
  $('#manualFilter')?.addEventListener('input', (event) => { state.filter = event.target.value; renderList(); });
  $('#manualRefreshButton')?.addEventListener('click', () => refreshManuals());

  document.addEventListener('keydown', (event) => {
    const panel = document.getElementById('page-manual');
    if (!panel || panel.classList.contains('hidden')) return;
    if (event.target instanceof HTMLInputElement) return;
    if (event.key === 'ArrowRight' || event.key === 'PageDown') gotoPage(state.current + 1, { smooth: true });
    else if (event.key === 'ArrowLeft' || event.key === 'PageUp') gotoPage(state.current - 1, { smooth: true });
    else if (event.key === '+' || event.key === '=') setScale(state.scale * 1.2);
    else if (event.key === '-') setScale(state.scale / 1.2);
  });
  window.addEventListener('resize', throttle(() => {
    if (state.doc && document.getElementById('page-manual')?.classList.contains('hidden') === false) applyZoomMode();
  }, 250));
}

function debounce(fn, ms) {
  let timer = 0;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}
