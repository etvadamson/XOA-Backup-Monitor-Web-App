const overallStatusEl = document.getElementById('overallStatus');
const summaryTextEl = document.getElementById('summaryText');
const groupedViewEl = document.getElementById('groupedView');
const tableViewEl = document.getElementById('tableView');
const lastRefreshEl = document.getElementById('lastRefreshText');
const totalVmCountEl = document.getElementById('totalVmCount');
const instancePillsEl = document.getElementById('instancePills');
const allVmsTableBodyEl = document.getElementById('allVmsTableBody');

const statusColors = {
  Success: '#2ecc71',
  Warning: '#f39c12',
  Failed: '#e74c3c',
  Error: '#9b59b6',
  Unknown: '#95a5a6'
};

let currentView = localStorage.getItem('xoaView') || 'grouped';
let currentTheme = localStorage.getItem('xoaTheme') || 'dark';
let sortKey = 'instanceName';
let sortAsc = true;
let latestStatusData = null;

const expandOverrides = new Map();

applyTheme(currentTheme);
applyView(currentView);

function applyTheme(theme) {
  document.body.setAttribute('data-theme', theme);
  const btn = document.getElementById('themeToggleBtn');
  btn.textContent = theme === 'dark' ? 'Dark' : 'Light';
  localStorage.setItem('xoaTheme', theme);
}

function applyView(view) {
  const btn = document.getElementById('viewToggleBtn');
  if (view === 'table') {
    groupedViewEl.classList.add('hidden');
    tableViewEl.classList.remove('hidden');
    btn.textContent = 'Switch to Grouped View';
  } else {
    groupedViewEl.classList.remove('hidden');
    tableViewEl.classList.add('hidden');
    btn.textContent = 'Switch to Table View';
  }
  localStorage.setItem('xoaView', view);
}

document.getElementById('themeToggleBtn').addEventListener('click', () => {
  currentTheme = currentTheme === 'dark' ? 'light' : 'dark';
  applyTheme(currentTheme);
});

document.getElementById('viewToggleBtn').addEventListener('click', () => {
  currentView = currentView === 'grouped' ? 'table' : 'grouped';
  applyView(currentView);
  if (latestStatusData) renderStatus(latestStatusData);
});

document.getElementById('collapseAllBtn').addEventListener('click', () => {
  if (!latestStatusData || !latestStatusData.groups) return;
  const anyExpanded = latestStatusData.groups.some(g => resolveExpanded(g));
  latestStatusData.groups.forEach(g => expandOverrides.set(g.instanceName, !anyExpanded));
  renderStatus(latestStatusData);
});

function resolveExpanded(group) {
  if (expandOverrides.has(group.instanceName)) {
    return expandOverrides.get(group.instanceName);
  }
  return group.statusText !== 'ALL OK';
}

async function fetchStatus() {
  const res = await fetch('/api/status');
  const data = await res.json();
  latestStatusData = data;
  renderStatus(data);
}

function renderStatus(data) {
  overallStatusEl.textContent = data.overallStatus;
  overallStatusEl.style.background = statusColors[data.overallStatus] || '#95a5a6';
  summaryTextEl.textContent = data.summary;
  lastRefreshEl.textContent = data.lastRefresh
    ? `Last refresh: ${new Date(data.lastRefresh).toLocaleString()}`
    : 'Not refreshed yet';
  totalVmCountEl.textContent = `Total VMs: ${data.totalVmCount}`;

  if (currentView === 'table') {
    renderTableView(data);
  } else {
    renderGroupedView(data);
  }
}

function renderGroupedView(data) {
  groupedViewEl.innerHTML = '';

  if (!data.groups || data.groups.length === 0) {
    groupedViewEl.innerHTML = '<p style="color:#909090">No data yet. Add an XOA instance via Configure, then click Refresh Now.</p>';
    return;
  }

  for (const group of data.groups) {
    const isExpanded = resolveExpanded(group);

    const card = document.createElement('div');
    card.className = 'group-card';

    const header = document.createElement('div');
    header.className = 'group-header';
    header.innerHTML = `
      <button class="icon-btn expand-toggle" data-instance="${escapeHtml(group.instanceName)}" title="Expand/collapse">${isExpanded ? '&#9660;' : '&#9654;'}</button>
      ${group.instanceUrl
        ? `<a href="${escapeHtml(group.instanceUrl)}" target="_blank" rel="noopener noreferrer" class="instance-link name">${escapeHtml(group.instanceName)}</a>`
        : `<span class="name">${escapeHtml(group.instanceName)}</span>`}
      <button class="icon-btn" title="Refresh this instance" data-instance="${escapeHtml(group.instanceName)}">&#128260;</button>
      <span class="spacer"></span>
      <span class="status-chip" style="background:${group.statusColor}">${escapeHtml(group.statusText)}</span>
    `;
    card.appendChild(header);

    const summary = document.createElement('div');
    summary.className = 'group-summary';
    summary.textContent = group.summary;
    card.appendChild(summary);

    if (isExpanded) {
      const columnHeader = document.createElement('div');
      columnHeader.className = 'vm-header';
      columnHeader.innerHTML = `
        <span>VM Name</span>
        <span>Status</span>
        <span>Last Backup</span>
        <span>Hours Ago</span>
        <span>Message</span>
      `;
      card.appendChild(columnHeader);

      for (const vm of group.vms) {
        const row = document.createElement('div');
        row.className = `vm-row status-${vm.status}`;
        row.innerHTML = `
          <span><a href="#" class="vm-link" data-instance="${escapeHtml(group.instanceName)}" data-vm="${escapeHtml(vm.vmName)}">${escapeHtml(vm.vmName)}</a></span>
          <span class="status-chip" style="background:${vm.statusColor}">${escapeHtml(vm.statusText)}</span>
          <span>${escapeHtml(vm.formattedLastBackup)}</span>
          <span>${typeof vm.ageInHours === 'number' ? vm.ageInHours.toFixed(1) : ''} hrs</span>
          <span>${escapeHtml(vm.message)}</span>
        `;
        card.appendChild(row);
      }
    }

    groupedViewEl.appendChild(card);
  }

  document.querySelectorAll('.expand-toggle').forEach(btn => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const name = e.currentTarget.getAttribute('data-instance');
      const group = latestStatusData.groups.find(g => g.instanceName === name);
      expandOverrides.set(name, !resolveExpanded(group));
      renderStatus(latestStatusData);
    });
  });

  document.querySelectorAll('.group-header > .icon-btn[data-instance]:not(.expand-toggle)').forEach(btn => {
    btn.addEventListener('click', async (e) => {
      const name = e.currentTarget.getAttribute('data-instance');
      await fetch(`/api/refresh/${encodeURIComponent(name)}`, { method: 'POST' });
      fetchStatus();
    });
  });

  document.querySelectorAll('.vm-link').forEach(link => {
    link.addEventListener('click', (e) => {
      e.preventDefault();
      const instance = e.currentTarget.getAttribute('data-instance');
      const vm = e.currentTarget.getAttribute('data-vm');
      openHistoryModal(instance, vm);
    });
  });
}

function flattenVms(data) {
  const rows = [];
  for (const group of (data.groups || [])) {
    for (const vm of group.vms) {
      rows.push({ ...vm, instanceName: group.instanceName, instanceUrl: group.instanceUrl });
    }
  }
  return rows;
}

function renderTableView(data) {
  instancePillsEl.innerHTML = (data.groups || []).map(g => `
    <span class="instance-pill" style="background:${g.statusColor}">
      ${g.instanceUrl
        ? `<a href="${escapeHtml(g.instanceUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtml(g.instanceName)}</a>`
        : escapeHtml(g.instanceName)}
      <button class="icon-btn" data-instance="${escapeHtml(g.instanceName)}" title="Refresh">&#128260;</button>
    </span>
  `).join('');

  instancePillsEl.querySelectorAll('[data-instance]').forEach(btn => {
    btn.addEventListener('click', async (e) => {
      const name = e.currentTarget.getAttribute('data-instance');
      await fetch(`/api/refresh/${encodeURIComponent(name)}`, { method: 'POST' });
      fetchStatus();
    });
  });

  let rows = flattenVms(data);

  rows.sort((a, b) => {
    let av = a[sortKey];
    let bv = b[sortKey];
    if (typeof av === 'string') av = av.toLowerCase();
    if (typeof bv === 'string') bv = bv.toLowerCase();
    if (av < bv) return sortAsc ? -1 : 1;
    if (av > bv) return sortAsc ? 1 : -1;
    return 0;
  });

  allVmsTableBodyEl.innerHTML = rows.map(vm => `
    <tr class="status-${vm.status}">
      <td><span class="status-dot" style="background:${vm.statusColor}"></span></td>
      <td>${escapeHtml(vm.instanceName)}</td>
      <td><a href="#" class="vm-link" data-instance="${escapeHtml(vm.instanceName)}" data-vm="${escapeHtml(vm.vmName)}">${escapeHtml(vm.vmName)}</a></td>
      <td>${escapeHtml(vm.statusText)}</td>
      <td>${escapeHtml(vm.formattedLastBackup)}</td>
      <td>${typeof vm.ageInHours === 'number' ? vm.ageInHours.toFixed(1) : ''}</td>
      <td>${escapeHtml(vm.message)}</td>
    </tr>
  `).join('');

  allVmsTableBodyEl.querySelectorAll('.vm-link').forEach(link => {
    link.addEventListener('click', (e) => {
      e.preventDefault();
      const instance = e.currentTarget.getAttribute('data-instance');
      const vm = e.currentTarget.getAttribute('data-vm');
      openHistoryModal(instance, vm);
    });
  });
}

document.querySelectorAll('#allVmsTable th[data-key]').forEach(th => {
  th.addEventListener('click', () => {
    const key = th.getAttribute('data-key');
    if (key === 'statusDot') return;
    if (sortKey === key) {
      sortAsc = !sortAsc;
    } else {
      sortKey = key;
      sortAsc = true;
    }
    if (latestStatusData) renderTableView(latestStatusData);
  });
});

function escapeHtml(str) {
  if (str === null || str === undefined) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

document.getElementById('refreshBtn').addEventListener('click', async () => {
  await fetch('/api/refresh', { method: 'POST' });
  fetchStatus();
});

document.getElementById('exportBtn').addEventListener('click', () => {
  window.location.href = '/api/export/csv';
});

const configModal = document.getElementById('configModal');
const instanceTableBody = document.getElementById('instanceTableBody');
const instanceForm = document.getElementById('instanceForm');
const testResult = document.getElementById('testResult');

document.getElementById('configureBtn').addEventListener('click', async () => {
  configModal.classList.remove('hidden');
  await loadInstances();
  await loadSettings();
});

document.getElementById('closeConfigBtn').addEventListener('click', () => {
  configModal.classList.add('hidden');
  fetchStatus();
});

async function loadSettings() {
  const res = await fetch('/api/settings');
  const data = await res.json();
  document.getElementById('refreshIntervalInput').value = data.refreshIntervalMinutes;
}

document.getElementById('saveIntervalBtn').addEventListener('click', async () => {
  const minutes = parseInt(document.getElementById('refreshIntervalInput').value, 10);
  await fetch('/api/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshIntervalMinutes: minutes })
  });
});

async function loadInstances() {
  const res = await fetch('/api/instances');
  const instances = await res.json();
  instanceTableBody.innerHTML = '';

  for (const inst of instances) {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>${escapeHtml(inst.name)}</td>
      <td>${escapeHtml(inst.url)}</td>
      <td>${inst.isEnabled ? 'Yes' : 'No'}</td>
      <td>${inst.hasToken ? 'Yes' : 'No'}</td>
      <td><button class="small-btn" data-delete="${escapeHtml(inst.name)}">Delete</button></td>
    `;
    instanceTableBody.appendChild(tr);
  }

  document.querySelectorAll('[data-delete]').forEach(btn => {
    btn.addEventListener('click', async (e) => {
      const name = e.currentTarget.getAttribute('data-delete');
      await fetch(`/api/instances/${encodeURIComponent(name)}`, { method: 'DELETE' });
      await loadInstances();
      await fetchStatus();
    });
  });
}

instanceForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const body = {
    name: document.getElementById('instanceName').value,
    url: document.getElementById('instanceUrl').value,
    apiToken: document.getElementById('instanceToken').value,
    isEnabled: document.getElementById('instanceEnabled').checked
  };

  const saveBtn = e.target.querySelector('button[type="submit"]');
  const originalText = saveBtn ? saveBtn.textContent : '';
  if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving...'; }

  await fetch('/api/instances', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });

  document.getElementById('instanceToken').value = '';
  await loadInstances();

  if (saveBtn) { saveBtn.textContent = 'Refreshing data...'; }
  await fetch(`/api/refresh/${encodeURIComponent(body.name)}`, { method: 'POST' });
  await fetchStatus();

  if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = originalText; }
});

document.getElementById('testConnectionBtn').addEventListener('click', async () => {
  const url = document.getElementById('instanceUrl').value;
  const token = document.getElementById('instanceToken').value;

  if (!url || !token) {
    testResult.textContent = 'Enter URL and API Token first, then Test.';
    testResult.style.color = '#f39c12';
    return;
  }

  testResult.textContent = 'Testing...';
  testResult.style.color = '';
  const res = await fetch('/api/test-connection', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url, apiToken: token })
  });
  const data = await res.json();
  testResult.textContent = data.success ? 'Connection OK' : 'Connection failed';
  testResult.style.color = data.success ? '#2ecc71' : '#e74c3c';
});

const historyModal = document.getElementById('historyModal');
const historyTitle = document.getElementById('historyTitle');
const historyTableBody = document.getElementById('historyTableBody');
const historyStats = document.getElementById('historyStats');
const historyRangeFilter = document.getElementById('historyRangeFilter');
const historyStatusFilter = document.getElementById('historyStatusFilter');

let currentHistoryEntries = [];

async function openHistoryModal(instanceName, vmName) {
  historyTitle.textContent = `Backup History - ${vmName}`;
  historyTableBody.innerHTML = '<tr><td colspan="3">Loading...</td></tr>';
  historyStats.textContent = '';
  historyModal.classList.remove('hidden');

  const res = await fetch(`/api/history?instance=${encodeURIComponent(instanceName)}&vm=${encodeURIComponent(vmName)}`);

  if (!res.ok) {
    historyTableBody.innerHTML = '<tr><td colspan="3">Failed to load history.</td></tr>';
    return;
  }

  currentHistoryEntries = await res.json();
  renderHistory();
}

function renderHistory() {
  const rangeDays = parseInt(historyRangeFilter.value, 10);
  const statusFilter = historyStatusFilter.value;

  let filtered = currentHistoryEntries;

  if (rangeDays > 0) {
    const cutoff = new Date();
    cutoff.setDate(cutoff.getDate() - rangeDays);
    filtered = filtered.filter(e => new Date(e.timestamp) >= cutoff);
  }

  if (statusFilter !== 'all') {
    filtered = filtered.filter(e => e.status === statusFilter);
  }

  const total = filtered.length;
  const success = filtered.filter(e => e.status === 'Success').length;
  const warning = filtered.filter(e => e.status === 'Warning').length;
  const failed = filtered.filter(e => e.status === 'Failed').length;
  const successRate = total > 0 ? ((success / total) * 100).toFixed(1) : '0.0';

  historyStats.innerHTML = `
    <span>Total: <strong>${total}</strong></span>
    <span style="color:#2ecc71">Success: <strong>${success}</strong></span>
    <span style="color:#f39c12">Warning: <strong>${warning}</strong></span>
    <span style="color:#e74c3c">Failed: <strong>${failed}</strong></span>
    <span>Success Rate: <strong>${successRate}%</strong></span>
  `;

  if (filtered.length === 0) {
    historyTableBody.innerHTML = '<tr><td colspan="3">No history entries for this filter.</td></tr>';
    return;
  }

  historyTableBody.innerHTML = filtered.map(e => `
    <tr>
      <td>${new Date(e.timestamp).toLocaleString()}</td>
      <td><span class="status-chip" style="background:${statusColors[e.status] || '#95a5a6'}">${escapeHtml(e.statusText)}</span></td>
      <td>${escapeHtml(e.message)}</td>
    </tr>
  `).join('');
}

historyRangeFilter.addEventListener('change', renderHistory);
historyStatusFilter.addEventListener('change', renderHistory);

document.getElementById('closeHistoryBtn').addEventListener('click', () => {
  historyModal.classList.add('hidden');
});

fetchStatus();
setInterval(fetchStatus, 30000);
