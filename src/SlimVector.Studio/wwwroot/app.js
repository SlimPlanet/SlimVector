(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const state = {
    bootstrap: null,
    collection: localStorage.getItem('slimvector.collection') || 'documents',
    files: [],
    searchMode: 'hybrid',
    documentOffset: 0,
    documentLimit: 25,
    documentTotal: 0,
    selectedDocuments: new Set(),
  };

  const escapeHtml = (value) => String(value ?? '')
    .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;').replaceAll("'", '&#039;');
  const formatNumber = (value) => new Intl.NumberFormat('fr-FR').format(Number(value || 0));
  const formatBytes = (value) => {
    let size = Number(value || 0);
    const units = ['o', 'Ko', 'Mo', 'Go'];
    let unit = 0;
    while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; }
    return `${size.toLocaleString('fr-FR', { maximumFractionDigits: 1 })} ${units[unit]}`;
  };
  const formatDate = (value) => value ? new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—';
  const shorten = (value, length = 90) => String(value ?? '').length > length ? `${String(value).slice(0, length)}…` : String(value ?? '');

  async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (options.body && !(options.body instanceof FormData) && typeof options.body !== 'string') {
      headers.set('Content-Type', 'application/json');
      options.body = JSON.stringify(options.body);
    }
    const response = await fetch(`/studio/api${path}`, { ...options, headers });
    if (!response.ok) {
      let problem;
      try { problem = await response.json(); } catch { problem = {}; }
      const error = new Error(problem.detail || problem.title || `Erreur HTTP ${response.status}`);
      error.code = problem.code || problem.extensions?.code || 'http_error';
      error.status = response.status;
      throw error;
    }
    if (response.status === 204 || !response.headers.get('content-type')?.includes('json')) return null;
    return response.json();
  }

  function toast(title, message = '', type = 'success') {
    const element = document.createElement('div');
    element.className = `toast ${type}`;
    element.innerHTML = `<div><strong>${escapeHtml(title)}</strong><span>${escapeHtml(message)}</span></div>`;
    $('#toast-stack').append(element);
    setTimeout(() => element.remove(), 4800);
  }

  function showBusy(title, message, hint = 'Le traitement reste entièrement local.') {
    $('#busy-title').textContent = title;
    $('#busy-message').textContent = message;
    $('#busy-hint').textContent = hint;
    $('#busy-overlay').hidden = false;
  }
  function hideBusy() { $('#busy-overlay').hidden = true; }

  function navigate(view, updateHash = true) {
    const target = $(`#view-${view}`) ? view : 'overview';
    $$('.view').forEach(item => item.classList.toggle('active', item.id === `view-${target}`));
    $$('.nav-item').forEach(item => item.classList.toggle('active', item.dataset.view === target));
    $('#sidebar').classList.remove('open');
    if (updateHash) history.replaceState(null, '', `#${target}`);
    window.scrollTo({ top: 0, behavior: 'smooth' });
    if (target === 'documents') loadDocuments();
    if (target === 'operations') loadRuntime();
  }

  function collectionByName(name = state.collection) {
    return state.bootstrap?.collections.find(item => item.definition.name === name);
  }

  function syncCollectionSelectors() {
    const collections = state.bootstrap?.collections || [];
    if (!collections.some(item => item.definition.name === state.collection)) {
      state.collection = collections[0]?.definition.name || '';
    }
    const options = collections.map(item => `<option value="${escapeHtml(item.definition.name)}">${escapeHtml(item.definition.name)} · ${item.definition.dimension}d</option>`).join('');
    ['global-collection', 'ingest-collection'].forEach(id => {
      const select = $(`#${id}`);
      select.innerHTML = options || '<option value="">Aucune collection</option>';
      select.value = state.collection;
    });
    localStorage.setItem('slimvector.collection', state.collection);
  }

  async function loadBootstrap({ quiet = false } = {}) {
    if (!quiet) {
      $('#overview-collections').innerHTML = '<div class="skeleton"></div><div class="skeleton"></div><div class="skeleton"></div>';
    }
    try {
      state.bootstrap = await api('/bootstrap');
      syncCollectionSelectors();
      renderBootstrap();
      return state.bootstrap;
    } catch (error) {
      setDatabaseState(false, 'Base indisponible', error.message);
      if (!quiet) toast('Impossible de joindre SlimVector', error.message, 'error');
      throw error;
    }
  }

  function setDatabaseState(ready, label, detail) {
    const dot = $('#sidebar-db-dot');
    dot.className = `status-dot ${ready ? 'ready' : 'error'}`;
    $('#sidebar-db-status').textContent = label;
    $('#sidebar-db-detail').textContent = detail;
  }

  function renderBootstrap() {
    const data = state.bootstrap;
    const total = data.collections.reduce((sum, item) => sum + item.documentCount, 0);
    const active = collectionByName();
    $('#stat-collections').textContent = formatNumber(data.collections.length);
    $('#stat-documents').textContent = formatNumber(active?.documentCount ?? total);
    $('#stat-active-count').textContent = active ? `${formatNumber(total)} au total` : 'toutes collections';
    $('#search-collection-count').textContent = formatNumber(active?.documentCount || 0);
    $('#collections-count-badge').textContent = data.collections.length;
    $('#stat-model').textContent = `${data.model.dimension}d`;
    $('#model-variant').textContent = data.model.variant.split('/').pop().replace('.onnx', '');
    setModelState(data.model);
    setDatabaseState(true, 'SlimVector prêt', `${data.collections.length} collection${data.collections.length > 1 ? 's' : ''}`);
    renderOverviewCollections();
    renderCollections();
  }

  function setModelState(model) {
    const ready = model.isReady;
    $('#model-pill').querySelector('.status-dot').className = `status-dot ${ready ? 'ready' : 'warning'}`;
    $('#model-card-dot').className = `status-dot ${ready ? 'ready' : 'warning'}`;
    $('#model-pill-label').textContent = ready ? 'Modèle local prêt' : 'Modèle à télécharger';
    $('#stat-model-status').textContent = ready ? 'ONNX prêt hors ligne' : 'premier lancement requis';
    $('#prepare-model-label').textContent = ready ? 'Modèle prêt' : 'Télécharger le modèle';
    $('#prepare-model').disabled = ready;
  }

  function renderOverviewCollections() {
    const target = $('#overview-collections');
    const collections = state.bootstrap?.collections || [];
    if (!collections.length) {
      target.innerHTML = '<div class="empty-state"><h3>Aucune collection</h3><p>Créez votre premier espace vectoriel.</p></div>';
      return;
    }
    target.innerHTML = collections.slice(0, 6).map(item => {
      const c = item.definition;
      return `<button class="collection-row" data-select-collection="${escapeHtml(c.name)}" style="width:100%;border-right:0;border-bottom:0;border-left:0;color:inherit;background:transparent;text-align:left">
        <span class="collection-identity"><span class="collection-avatar">${escapeHtml(c.name.slice(0, 2).toUpperCase())}</span><span><strong>${escapeHtml(c.name)}</strong><small>${escapeHtml(c.id.slice(0, 8))}</small></span></span>
        <span class="collection-cell"><strong>${formatNumber(item.documentCount)}</strong><small>chunks</small></span>
        <span class="collection-cell"><strong>${c.dimension}d</strong><small>${escapeHtml(c.metric)}</small></span>
        <span class="collection-cell"><span class="index-pill">${escapeHtml(c.vectorIndex.kind)}</span><small>index</small></span>
      </button>`;
    }).join('');
  }

  function setCollection(name) {
    if (!name || name === state.collection) return;
    state.collection = name;
    state.documentOffset = 0;
    state.selectedDocuments.clear();
    syncCollectionSelectors();
    renderBootstrap();
    toast('Collection active', name);
    const activeView = $('.view.active')?.id;
    if (activeView === 'view-documents') loadDocuments();
  }

  function renderCollections() {
    const target = $('#collections-grid');
    const collections = state.bootstrap?.collections || [];
    target.innerHTML = collections.map(item => {
      const c = item.definition;
      return `<article class="collection-card ${c.name === state.collection ? 'active' : ''}" data-collection-card="${escapeHtml(c.name)}">
        <div class="collection-card-head"><button class="collection-card-title" data-select-collection="${escapeHtml(c.name)}" style="border:0;padding:0;color:inherit;background:transparent;text-align:left">
          <span class="collection-avatar">${escapeHtml(c.name.slice(0, 2).toUpperCase())}</span><span><strong>${escapeHtml(c.name)}</strong><small>${escapeHtml(c.id)}</small></span></button>
          <div class="card-actions"><button class="icon-btn" data-edit-collection="${escapeHtml(c.name)}" title="Configurer"><svg><use href="#i-settings"/></svg></button><button class="icon-btn" data-delete-collection="${escapeHtml(c.name)}" title="Supprimer"><svg><use href="#i-trash"/></svg></button></div>
        </div>
        <div class="collection-specs"><div><span>Documents</span><strong>${formatNumber(item.documentCount)}</strong></div><div><span>Dimension</span><strong>${c.dimension}d</strong></div><div><span>Index</span><strong>${escapeHtml(c.vectorIndex.kind)}</strong></div></div>
      </article>`;
    }).join('') || '<div class="empty-state"><h3>Catalogue vide</h3></div>';
  }

  function renderFileQueue() {
    $('#file-queue').innerHTML = state.files.map((file, index) => {
      const ext = file.name.split('.').pop().toUpperCase();
      return `<div class="file-item"><span class="file-type">${escapeHtml(ext)}</span><div><strong>${escapeHtml(file.name)}</strong><small>${formatBytes(file.size)} · ${escapeHtml(file.type || 'document')}</small></div><button type="button" class="icon-btn" data-remove-file="${index}" aria-label="Retirer"><svg><use href="#i-x"/></svg></button></div>`;
    }).join('');
  }

  function acceptFiles(fileList) {
    const allowed = ['pdf', 'docx', 'pptx', 'txt', 'md', 'markdown'];
    const incoming = [...fileList].filter(file => allowed.includes(file.name.split('.').pop().toLowerCase()));
    const max = state.bootstrap?.maximumUploadBytes || 134217728;
    incoming.forEach(file => {
      if (file.size > max) toast('Fichier trop volumineux', `${file.name} dépasse ${formatBytes(max)}`, 'error');
      else if (!state.files.some(existing => existing.name === file.name && existing.size === file.size)) state.files.push(file);
    });
    renderFileQueue();
  }

  async function submitIngestion(event) {
    event.preventDefault();
    if (!state.files.length) { toast('Aucun document', 'Sélectionnez au moins un fichier.', 'error'); return; }
    if (!state.collection) { toast('Aucune collection', 'Créez ou sélectionnez une collection.', 'error'); return; }
    const form = event.currentTarget;
    const values = new FormData(form);
    const payload = new FormData();
    state.files.forEach(file => payload.append('files', file, file.name));
    payload.append('collection', $('#ingest-collection').value);
    ['strategy', 'targetTokens', 'maximumTokens', 'overlapTokens', 'metadata'].forEach(key => payload.append(key, values.get(key) || ''));
    ['replaceExisting', 'atomic', 'previewOnly'].forEach(key => payload.append(key, String(values.get(key) === 'on')));
    showBusy('Pipeline documentaire', `Extraction et vectorisation de ${state.files.length} fichier${state.files.length > 1 ? 's' : ''}…`, state.bootstrap?.model.isReady ? 'Le modèle ONNX est chargé localement.' : 'Premier lancement : téléchargement unique du modèle Hugging Face.');
    try {
      const results = await api('/ingest', { method: 'POST', body: payload });
      renderIngestResults(results);
      toast('Ingestion terminée', `${results.reduce((n, r) => n + r.chunkCount, 0)} chunks produits.`);
      await loadBootstrap({ quiet: true });
    } catch (error) {
      toast('Échec de l’ingestion', error.message, 'error');
    } finally { hideBusy(); }
  }

  function renderIngestResults(results) {
    const target = $('#ingest-result');
    target.className = 'result-zone';
    target.innerHTML = results.map(result => `<article class="ingest-document-result">
      <div class="results-header"><div><strong>${escapeHtml(result.fileName)}</strong><small>${escapeHtml(result.format)} · ${result.sectionCount} section(s) · ${formatNumber(result.characterCount)} caractères</small></div><span class="query-time">${Math.round(result.embeddingMilliseconds)} ms embedding</span></div>
      <div class="ingest-summary"><div class="summary-cell"><span>Chunks</span><strong>${result.chunkCount}</strong></div><div class="summary-cell"><span>Stockés</span><strong>${result.storedCount}</strong></div><div class="summary-cell"><span>Remplacés</span><strong>${result.removedPreviousCount}</strong></div><div class="summary-cell"><span>SHA-256</span><strong title="${escapeHtml(result.contentSha256)}">${escapeHtml(result.contentSha256.slice(0, 8))}</strong></div></div>
      <div class="chunk-list">${result.chunks.map(chunk => `<details class="chunk-card"><summary><span class="chunk-no">${String(chunk.sequence + 1).padStart(2, '0')}</span><span class="chunk-head"><strong>${escapeHtml(chunk.heading || chunk.locations.join(' · '))}</strong><small>${escapeHtml(shorten(chunk.text, 115))}</small></span><span class="chunk-tokens">${chunk.estimatedTokens} tok.</span></summary><div class="chunk-content"><p>${escapeHtml(chunk.text)}</p><div class="vector-preview">[${chunk.vectorPreview.map(value => Number(value).toFixed(5)).join(', ')}, …]</div></div></details>`).join('')}</div>
    </article>`).join('');
  }

  function setSearchMode(mode) {
    state.searchMode = mode;
    $('#search-form [name="mode"]').value = mode;
    $$('.mode-tabs button').forEach(button => button.classList.toggle('active', button.dataset.mode === mode));
    const needsQuery = mode !== 'metadata';
    $('#search-form [name="query"]').placeholder = needsQuery ? 'Ex. Comment fonctionne la réplication géographique ?' : 'Optionnel en mode métadonnées';
    $$('.weight-field').forEach(field => field.style.display = mode === 'hybrid' ? '' : 'none');
    $('#query-model-hint').textContent = ['vector', 'hybrid'].includes(mode) ? 'Vectorisation locale 384d' : mode === 'text' ? 'Index BM25 persistant' : 'Filtre indexé';
  }

  function parseFilter(form) {
    if (!$('#filter-enabled').checked) return null;
    const field = form.elements.filterField.value.trim();
    const operator = form.elements.filterOperator.value;
    if (!field) throw new Error('Le champ du filtre est requis.');
    const filter = { operator, field };
    if (operator === 'exists') return filter;
    const raw = form.elements.filterValue.value.trim();
    if (!raw) throw new Error('La valeur du filtre est requise.');
    let parsed;
    try { parsed = JSON.parse(raw); } catch { parsed = raw; }
    if (operator === 'in') filter.values = Array.isArray(parsed) ? parsed : String(parsed).split(',').map(item => item.trim());
    else filter.value = parsed;
    return filter;
  }

  async function submitSearch(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const query = form.elements.query.value.trim();
    if (state.searchMode !== 'metadata' && !query) { toast('Requête vide', 'Saisissez un texte à rechercher.', 'error'); return; }
    let filter;
    try { filter = parseFilter(form); } catch (error) { toast('Filtre invalide', error.message, 'error'); return; }
    const vectorWeight = Number(form.elements.vectorWeight.value) / 100;
    const textWeight = Number(form.elements.textWeight.value) / 100;
    const body = {
      query,
      mode: state.searchMode,
      limit: Number(form.elements.limit.value),
      consistency: form.elements.consistency.value,
      vectorWeight,
      textWeight,
      filter,
      includeText: form.elements.includeText.checked,
      includeMetadata: form.elements.includeMetadata.checked,
      includeScores: form.elements.includeScores.checked,
      includeVector: form.elements.includeVector.checked,
    };
    $('#search-results').innerHTML = '<div class="empty-state tall"><div class="loader-orbit"><span></span><span></span><i></i></div><h2>Recherche en cours</h2><p>Interrogation des index persistants…</p></div>';
    try {
      const result = await api(`/collections/${encodeURIComponent(state.collection)}/search`, { method: 'POST', body });
      renderSearchResults(result);
    } catch (error) {
      $('#search-results').innerHTML = `<div class="empty-state tall"><svg style="color:var(--danger)"><use href="#i-x"/></svg><h2>Requête refusée</h2><p>${escapeHtml(error.message)}</p></div>`;
      toast('Échec de la recherche', error.message, 'error');
    }
  }

  function renderSearchResults(result) {
    const target = $('#search-results');
    if (!result.hits.length) {
      target.innerHTML = '<div class="empty-state tall"><svg><use href="#i-search"/></svg><h2>Aucun résultat</h2><p>Élargissez votre requête ou retirez le filtre de métadonnées.</p></div>';
      return;
    }
    target.innerHTML = `<div class="results-header"><div><strong>${result.hits.length} résultat${result.hits.length > 1 ? 's' : ''}</strong><small>${result.queryWasVectorized ? 'Requête vectorisée localement · ' : ''}${escapeHtml(state.searchMode)}</small></div><span class="query-time">${(result.tookMicroseconds / 1000).toFixed(2)} ms</span></div><div class="hit-list">${result.hits.map((hit, index) => {
      const metadata = hit.metadata || {};
      const chips = Object.entries(metadata).slice(0, 4).map(([key, value]) => `<span class="meta-chip">${escapeHtml(key)}: ${escapeHtml(shorten(Array.isArray(value) ? value.join(', ') : value, 35))}</span>`).join('');
      return `<article class="hit-card"><div class="hit-top"><span class="hit-id"><b style="color:var(--muted-2);margin-right:7px">#${index + 1}</b>${escapeHtml(hit.id)}</span>${hit.score == null ? '' : `<span class="score-pill">${Number(hit.score).toFixed(5)}</span>`}</div>${hit.text == null ? '' : `<p class="hit-text">${escapeHtml(hit.text)}</p>`}<div class="hit-bottom"><div class="rank-list">${hit.vectorRank ? `<span>vecteur #${hit.vectorRank}</span>` : ''}${hit.textRank ? `<span>BM25 #${hit.textRank}</span>` : ''}</div><div class="meta-chips">${chips}</div></div>${Object.keys(metadata).length || hit.vector ? `<details class="hit-details"><summary>Données complètes</summary><pre class="json-block">${escapeHtml(JSON.stringify({ metadata, vector: hit.vector }, null, 2))}</pre></details>` : ''}</article>`;
    }).join('')}</div>`;
  }

  async function loadDocuments() {
    if (!state.collection) return;
    const tbody = $('#documents-body');
    tbody.innerHTML = '<tr><td colspan="6"><div class="skeleton"></div></td></tr>';
    try {
      const includeVectors = $('#include-vectors').checked;
      const data = await api(`/collections/${encodeURIComponent(state.collection)}/documents?offset=${state.documentOffset}&limit=${state.documentLimit}&includeVectors=${includeVectors}`);
      state.documentTotal = data.total;
      state.selectedDocuments.clear();
      renderDocuments(data);
    } catch (error) {
      tbody.innerHTML = `<tr><td colspan="6">${escapeHtml(error.message)}</td></tr>`;
      toast('Impossible de charger les documents', error.message, 'error');
    }
  }

  function renderDocuments(data) {
    const start = data.total ? data.offset + 1 : 0;
    const end = Math.min(data.offset + data.documents.length, data.total);
    $('#documents-total').textContent = `${formatNumber(data.total)} élément${data.total > 1 ? 's' : ''}`;
    $('#documents-range').textContent = `${state.collection} · ${start}–${end}`;
    $('#documents-page').textContent = `Page ${Math.floor(data.offset / data.limit) + 1}`;
    $('#documents-prev').disabled = data.offset === 0;
    $('#documents-next').disabled = end >= data.total;
    $('#delete-selected').disabled = true;
    $('#select-all-documents').checked = false;
    $('#documents-body').innerHTML = data.documents.map(document => {
      const source = document.metadata.source_file || 'manuel';
      const format = document.metadata.document_format || '—';
      return `<tr><td class="checkbox-cell"><input type="checkbox" data-document-id="${escapeHtml(document.id)}" aria-label="Sélectionner"></td><td><div class="doc-main"><strong title="${escapeHtml(document.id)}">${escapeHtml(document.id)}</strong><span>${escapeHtml(document.text)}</span><details class="hit-details"><summary>Métadonnées</summary><pre class="json-block">${escapeHtml(JSON.stringify(document.metadata, null, 2))}</pre></details></div></td><td><div class="source-cell"><strong>${escapeHtml(source)}</strong><small>${escapeHtml(format)}</small></div></td><td><span class="dimension-pill">${document.vectorDimension}d</span>${document.vector ? `<details class="hit-details"><summary>voir</summary><pre class="json-block">${escapeHtml(JSON.stringify(document.vector))}</pre></details>` : ''}</td><td>v${document.version}</td><td>${formatDate(document.updatedAt)}</td></tr>`;
    }).join('') || '<tr><td colspan="6"><div class="empty-state"><h3>Collection vide</h3><p>Ingestérez un document ou ajoutez une mutation manuelle.</p></div></td></tr>';
  }

  async function deleteSelectedDocuments() {
    const ids = [...state.selectedDocuments];
    if (!ids.length || !confirm(`Supprimer définitivement ${ids.length} document(s) de ${state.collection} ?`)) return;
    showBusy('Suppression', `Application d’un batch atomique de ${ids.length} suppression(s)…`);
    try {
      const result = await api(`/collections/${encodeURIComponent(state.collection)}/documents/delete`, { method: 'POST', body: { ids, atomic: true } });
      toast('Documents supprimés', `${result.succeeded} mutation(s) confirmée(s).`);
      await Promise.all([loadDocuments(), loadBootstrap({ quiet: true })]);
    } catch (error) { toast('Suppression refusée', error.message, 'error'); } finally { hideBusy(); }
  }

  async function submitManualDocument(event) {
    event.preventDefault();
    const form = event.currentTarget;
    let metadata = null;
    let vector = null;
    try {
      if (form.elements.metadata.value.trim()) metadata = JSON.parse(form.elements.metadata.value);
      if (form.elements.vector.value.trim()) vector = JSON.parse(form.elements.vector.value);
    } catch (error) { toast('JSON invalide', error.message, 'error'); return; }
    const body = {
      kind: form.elements.kind.value,
      atomic: form.elements.atomic.checked,
      documents: [{ id: form.elements.id.value.trim(), text: form.elements.text.value || null, vector, autoVectorize: form.elements.autoVectorize.checked, metadata }],
    };
    showBusy('Mutation manuelle', `${body.kind} sur ${state.collection}…`);
    try {
      const result = await api(`/collections/${encodeURIComponent(state.collection)}/documents/mutate`, { method: 'POST', body });
      $('#document-dialog').close();
      form.reset();
      toast('Mutation appliquée', `${result.succeeded} succès, ${result.failed} échec.`);
      await Promise.all([loadDocuments(), loadBootstrap({ quiet: true })]);
    } catch (error) { toast('Mutation refusée', error.message, 'error'); } finally { hideBusy(); }
  }

  async function createCollection(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const body = Object.fromEntries(new FormData(form));
    ['dimension', 'hnswM', 'hnswEfConstruction', 'hnswEfSearch'].forEach(key => body[key] = Number(body[key]));
    showBusy('Création de la collection', `Initialisation de ${body.name}…`);
    try {
      const result = await api('/collections', { method: 'POST', body });
      state.collection = result.name;
      form.reset();
      form.elements.dimension.value = '384';
      toast('Collection créée', `${result.name} · ${result.dimension} dimensions`);
      await loadBootstrap({ quiet: true });
    } catch (error) { toast('Création refusée', error.message, 'error'); } finally { hideBusy(); }
  }

  function openEditCollection(name) {
    const item = collectionByName(name);
    if (!item) return;
    const c = item.definition;
    const form = $('#edit-collection-form');
    form.elements.currentName.value = c.name;
    form.elements.name.value = c.name;
    form.elements.indexKind.value = c.vectorIndex.kind;
    form.elements.hnswM.value = c.vectorIndex.hnswM;
    form.elements.hnswEfConstruction.value = c.vectorIndex.hnswEfConstruction;
    form.elements.hnswEfSearch.value = c.vectorIndex.hnswEfSearch;
    $('#collection-dialog').showModal();
  }

  async function editCollection(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const currentName = form.elements.currentName.value;
    const body = {
      name: form.elements.name.value,
      indexKind: form.elements.indexKind.value,
      hnswM: Number(form.elements.hnswM.value),
      hnswEfConstruction: Number(form.elements.hnswEfConstruction.value),
      hnswEfSearch: Number(form.elements.hnswEfSearch.value),
    };
    try {
      const result = await api(`/collections/${encodeURIComponent(currentName)}`, { method: 'PATCH', body });
      if (state.collection === currentName) state.collection = result.name;
      $('#collection-dialog').close();
      toast('Collection mise à jour', result.name);
      await loadBootstrap({ quiet: true });
    } catch (error) { toast('Modification refusée', error.message, 'error'); }
  }

  async function deleteCollection(name) {
    const item = collectionByName(name);
    if (!item || !confirm(`Supprimer la collection « ${name} » et ses ${item.documentCount} document(s) ? Cette action est irréversible.`)) return;
    showBusy('Suppression de la collection', name);
    try {
      await api(`/collections/${encodeURIComponent(name)}`, { method: 'DELETE' });
      toast('Collection supprimée', name);
      await loadBootstrap({ quiet: true });
    } catch (error) { toast('Suppression refusée', error.message, 'error'); } finally { hideBusy(); }
  }

  async function prepareModel() {
    showBusy('Préparation du modèle', 'Téléchargement de la variante ONNX adaptée à votre processeur…', 'Cette opération unique peut prendre quelques minutes. Les lancements suivants seront hors ligne.');
    try {
      const model = await api('/model/prepare', { method: 'POST' });
      state.bootstrap.model = model;
      setModelState(model);
      toast('Modèle prêt', `${model.modelId} · ${model.dimension} dimensions`);
    } catch (error) { toast('Téléchargement impossible', error.message, 'error'); } finally { hideBusy(); }
  }

  async function loadRuntime() {
    try {
      const [runtime, backups] = await Promise.all([api('/runtime'), api('/backups')]);
      renderRuntime(runtime);
      renderBackups(backups);
    } catch (error) {
      $('#runtime-banner').innerHTML = `<span class="status-dot error"></span><div><strong>État indisponible</strong><small>${escapeHtml(error.message)}</small></div>`;
    }
  }

  function renderRuntime(runtime) {
    const banner = $('#runtime-banner');
    banner.innerHTML = `<span class="status-dot ${runtime.ready ? 'ready' : 'warning'}"></span><div><strong>${runtime.ready ? 'SlimVector est opérationnel' : 'Consensus en préparation'}</strong><small>${escapeHtml(runtime.mode)} · ${runtime.raftGroups.length} groupe(s) Raft · stockage persistant</small></div>`;
    $('#op-memory').textContent = formatBytes(runtime.managedMemoryBytes);
    $('#op-open').textContent = formatNumber(runtime.openCollections);
    $('#op-writes').textContent = formatNumber(runtime.writes.totalWrites);
    $('#op-queue').textContent = `file ${formatNumber(runtime.writes.queueDepth)} · ${formatNumber(runtime.writes.rejectedWrites)} rejet`;
    $('#op-searches').textContent = formatNumber(runtime.operations.searches);
    $('#op-failures').textContent = `${formatNumber(runtime.operations.searchFailures)} échec`;
    $('#stat-searches').textContent = formatNumber(runtime.operations.searches);
    $('#stat-latency').textContent = runtime.operations.searches ? `${(runtime.operations.searchMicroseconds / runtime.operations.searches / 1000).toFixed(2)} ms moyen` : 'aucune requête';
    $('#raft-mode').textContent = runtime.mode;
    $('#raft-groups').innerHTML = runtime.raftGroups.length ? runtime.raftGroups.map(group => `<div class="raft-row"><div class="raft-name"><strong>${escapeHtml(group.groupId)}</strong><small>${escapeHtml(group.localEndpoint)}</small></div><span class="raft-term">term ${group.term} · #${group.lastAppliedIndex}</span><span class="leader-pill">${group.isLeader ? 'leader' : 'follower'}</span></div>`).join('') : '<div class="empty-state"><h3>Coordination directe</h3><p>Le mode single-node n’a pas besoin d’un transport Raft réseau.</p></div>';
    const writes = runtime.writes;
    const max = Math.max(1, writes.totalWrites, writes.completedWrites, writes.totalBatchItems);
    $('#write-metrics').innerHTML = [
      ['Écritures terminées', writes.completedWrites, writes.completedWrites / max],
      ['Éléments batchés', writes.totalBatchItems, writes.totalBatchItems / max],
      ['Lots adaptatifs', writes.totalBatches, writes.totalBatches / Math.max(1, writes.totalBatches)],
      ['File courante', writes.queueDepth, Math.min(1, writes.queueDepth / 1000)],
    ].map(([label, value, ratio]) => `<div><div class="metric-bar-head"><span>${label}</span><strong>${formatNumber(value)}</strong></div><div class="bar-track"><span style="--value:${Math.max(2, ratio * 100)}%"></span></div></div>`).join('');
  }

  function renderBackups(backups) {
    $('#backups-body').innerHTML = backups.map(backup => `<tr><td><span class="hit-id" title="${escapeHtml(backup.backupId)}">${escapeHtml(shorten(backup.backupId, 22))}</span></td><td>${formatDate(backup.createdAt)}</td><td>${backup.collectionCount}</td><td>${formatNumber(backup.documentCount)}</td><td>${escapeHtml(backup.parentBackupId ? shorten(backup.parentBackupId, 12) : '—')}</td><td><div class="table-actions"><button class="table-action" data-backup-action="verify" data-backup-id="${escapeHtml(backup.backupId)}">Vérifier</button><button class="table-action" data-backup-action="collection" data-backup-id="${escapeHtml(backup.backupId)}">Restaurer une collection</button><button class="table-action" data-backup-action="full" data-backup-id="${escapeHtml(backup.backupId)}">Restauration complète</button></div></td></tr>`).join('') || '<tr><td colspan="6"><div class="empty-state"><h3>Aucune sauvegarde</h3><p>Créez un premier snapshot logique.</p></div></td></tr>';
  }

  async function createBackup() {
    showBusy('Sauvegarde en cours', 'Création du manifeste et déduplication des blobs…');
    try {
      const result = await api('/backups', { method: 'POST' });
      toast('Sauvegarde créée', `${result.collectionCount} collection(s), ${formatNumber(result.documentCount)} documents.`);
      await loadRuntime();
    } catch (error) { toast('Sauvegarde impossible', error.message, 'error'); } finally { hideBusy(); }
  }

  async function backupAction(action, id) {
    try {
      if (action === 'verify') {
        showBusy('Vérification', `Contrôle des checksums de ${shorten(id, 18)}…`);
        await api(`/backups/${encodeURIComponent(id)}/verify`, { method: 'POST' });
        toast('Sauvegarde valide', id);
      } else if (action === 'collection') {
        const collectionName = prompt('Nom de la collection à restaurer :', state.collection);
        if (!collectionName) return;
        const restoredName = prompt('Nouveau nom (laisser vide pour conserver le nom) :', `${collectionName}-restored`);
        showBusy('Restauration ciblée', collectionName);
        await api(`/backups/${encodeURIComponent(id)}/restore-collection`, { method: 'POST', body: { collectionName, restoredName: restoredName || null, overwrite: false } });
        toast('Collection restaurée', restoredName || collectionName);
        await loadBootstrap({ quiet: true });
      } else {
        const confirmation = prompt('Cette opération remplace tout le catalogue. Tapez RESTORE pour confirmer :');
        if (confirmation !== 'RESTORE') return;
        showBusy('Restauration complète', 'Remplacement du catalogue et des segments…');
        await api(`/backups/${encodeURIComponent(id)}/restore`, { method: 'POST', body: { confirm: confirmation } });
        toast('Restauration terminée', id);
        await loadBootstrap({ quiet: true });
      }
    } catch (error) { toast('Opération impossible', error.message, 'error'); } finally { hideBusy(); }
  }

  function bindEvents() {
    $$('.nav-item').forEach(button => button.addEventListener('click', () => navigate(button.dataset.view)));
    $$('[data-go]').forEach(button => button.addEventListener('click', () => navigate(button.dataset.go)));
    $('#quick-ingest').addEventListener('click', () => navigate('ingest'));
    $('#mobile-menu').addEventListener('click', () => $('#sidebar').classList.toggle('open'));
    $('#theme-toggle').addEventListener('click', () => {
      const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
      document.documentElement.dataset.theme = theme;
      localStorage.setItem('slimvector.theme', theme);
    });
    $('#global-collection').addEventListener('change', event => setCollection(event.target.value));
    $('#ingest-collection').addEventListener('change', event => setCollection(event.target.value));
    document.addEventListener('click', event => {
      const select = event.target.closest('[data-select-collection]');
      if (select) setCollection(select.dataset.selectCollection);
      const remove = event.target.closest('[data-remove-file]');
      if (remove) { state.files.splice(Number(remove.dataset.removeFile), 1); renderFileQueue(); }
      const edit = event.target.closest('[data-edit-collection]');
      if (edit) openEditCollection(edit.dataset.editCollection);
      const removeCollection = event.target.closest('[data-delete-collection]');
      if (removeCollection) deleteCollection(removeCollection.dataset.deleteCollection);
      const backup = event.target.closest('[data-backup-action]');
      if (backup) backupAction(backup.dataset.backupAction, backup.dataset.backupId);
    });
    const dropzone = $('#dropzone');
    dropzone.addEventListener('click', () => $('#file-input').click());
    dropzone.addEventListener('keydown', event => { if (['Enter', ' '].includes(event.key)) { event.preventDefault(); $('#file-input').click(); } });
    ['dragenter', 'dragover'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.add('dragover'); }));
    ['dragleave', 'drop'].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.classList.remove('dragover'); }));
    dropzone.addEventListener('drop', event => acceptFiles(event.dataTransfer.files));
    $('#file-input').addEventListener('change', event => acceptFiles(event.target.files));
    $('#ingest-form').addEventListener('submit', submitIngestion);
    $('#prepare-model').addEventListener('click', prepareModel);
    $$('.mode-tabs button').forEach(button => button.addEventListener('click', () => setSearchMode(button.dataset.mode)));
    $('#filter-enabled').addEventListener('change', event => $('#filter-fields').hidden = !event.target.checked);
    ['vectorWeight', 'textWeight'].forEach(name => {
      const range = $(`#search-form [name="${name}"]`);
      range.addEventListener('input', () => {
        $(`#${name === 'vectorWeight' ? 'vector' : 'text'}-weight-output`).textContent = `${range.value}%`;
        range.style.background = `linear-gradient(90deg,var(--accent) ${range.value}%,rgba(148,152,170,.2) ${range.value}%)`;
      });
    });
    $('#search-form').addEventListener('submit', submitSearch);
    $('#search-form [name="query"]').addEventListener('keydown', event => { if ((event.metaKey || event.ctrlKey) && event.key === 'Enter') $('#search-form').requestSubmit(); });
    $('#refresh-documents').addEventListener('click', loadDocuments);
    $('#include-vectors').addEventListener('change', loadDocuments);
    $('#documents-prev').addEventListener('click', () => { state.documentOffset = Math.max(0, state.documentOffset - state.documentLimit); loadDocuments(); });
    $('#documents-next').addEventListener('click', () => { if (state.documentOffset + state.documentLimit < state.documentTotal) state.documentOffset += state.documentLimit; loadDocuments(); });
    $('#documents-body').addEventListener('change', event => {
      const checkbox = event.target.closest('[data-document-id]');
      if (!checkbox) return;
      if (checkbox.checked) state.selectedDocuments.add(checkbox.dataset.documentId); else state.selectedDocuments.delete(checkbox.dataset.documentId);
      $('#delete-selected').disabled = state.selectedDocuments.size === 0;
    });
    $('#select-all-documents').addEventListener('change', event => {
      $$('[data-document-id]', $('#documents-body')).forEach(checkbox => {
        checkbox.checked = event.target.checked;
        if (checkbox.checked) state.selectedDocuments.add(checkbox.dataset.documentId); else state.selectedDocuments.delete(checkbox.dataset.documentId);
      });
      $('#delete-selected').disabled = state.selectedDocuments.size === 0;
    });
    $('#delete-selected').addEventListener('click', deleteSelectedDocuments);
    $('#new-document').addEventListener('click', () => $('#document-dialog').showModal());
    $('#manual-document-form').addEventListener('submit', submitManualDocument);
    $('#create-collection-form').addEventListener('submit', createCollection);
    $('#edit-collection-form').addEventListener('submit', editCollection);
    $$('[data-close-dialog]').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));
    $('#refresh-runtime').addEventListener('click', loadRuntime);
    $('#create-backup').addEventListener('click', createBackup);
    window.addEventListener('hashchange', () => navigate(location.hash.slice(1), false));
  }

  async function init() {
    document.documentElement.dataset.theme = localStorage.getItem('slimvector.theme') || 'dark';
    bindEvents();
    setSearchMode('hybrid');
    navigate(location.hash.slice(1) || 'overview', false);
    try {
      await loadBootstrap();
      await loadRuntime();
    } catch { /* The status panel already displays the startup error. */ }
  }

  init();
})();
