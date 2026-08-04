const overallStatusEl = document.getElementById('overallStatus');
const summaryTextEl = document.getElementById('summaryText');
const groupsEl = document.getElementById('groups');
const lastRefreshEl = document.getElementById('lastRefreshText');
const totalVmCountEl = document.getElementById('totalVmCount');

const statusColors = {
  Success: '#2ecc71',
  Warning: '#f39c12',
  Failed: '#e74c3c',
  Error: '#9b59b6',
  Unknown: '#95a5a6'
};

async function fetchStatus() {
  const res = await fetch('/api/status');
  const data = await res.json();
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

  groupsEl.innerHTML = '';

  if (!data.groups || data.groups.length === 0) {
    groupsEl.innerHTML = '<p style="color:#909090">No data yet. Add an XOA instance via Configure, then click Refresh Now.</p>';
    return;
  }

  for (const group of data.groups) {
    const card = document.createElement('div');
    card.className = 'group-card';

    const header = document.createElement('div');
    header.className = 'group-header';
    header.innerHTML = `
      <span class="name">${escapeHtml(group.instanceName)}</span>
      <button class="icon-btn" title="Refresh this instance" data-instance="${escapeHtml(group.instanceName)}">&#128260;</button>
      <span class="spacer"></span>
      <span class="status-chip" style="background:${group.statusColor}">${escapeHtml(group.statusText)}</span>
    `;
    card.appendChild(header);

    const summary = document.createElement('div');
    summary.className = 'group-summary';
    summary.textContent = group.summary;
    card.appendChild(summary);

    for (const vm of group.vms) {
      const row = document.createElement('div');
      row.className = `vm-row status-${vm.status}`;
      row.innerHTML = `
        <span>${escapeHtml(vm.vMName)}</span>
        <span class="status-chip" style="background:${vm.statusColor}">${escapeHtml(vm.statusText)}</span>
        <span>${escapeHtml(vm.formattedLastBackup)}</span>
        <span>${typeof vm.ageInHours === 'number' ? vm.ageInHours.toFixed(1) : ''} hrs</span>
        <span>${escapeHtml(vm.message)}</span>
      `;
      card.appendChild(row);
    }

    groupsEl.appendChild(card);
  }

  document.querySelectorAll('[data-instance]').forEach(btn => {
    btn.addEventListener('click', async (e) => {
      const name = e.currentTarget.getAttribute('data-instance');
      await fetch(`/api/refresh/${encodeURIComponent(name)}`, { method: 'POST' });
      fetchStatus();
    });
  });
}

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

fetchStatus();
setInterval(fetchStatus, 30000);
