'use strict';
const $ = id => document.getElementById(id);
const state = { usage: null, settings: null, runtime: null, pets: [], selected: null, page: 0, total: 0, petLoaded: false, deviceTargetId: '', deviceStatuses: {}, deviceInfos: {} };
const escapeHtml = value => { const node = document.createElement('span'); node.textContent = String(value ?? ''); return node.innerHTML; };
let toastTimer, previewSequence = 0, petSearchSequence = 0, polling = false, loginRefreshedAt = sessionStorage.getItem('codex-login-refreshed-at'), codexLoginCode = '';
let messageFormLoaded = false, messagePolling = false, messageSending = false, messagesState = null;
function toast(message, error = false) { clearTimeout(toastTimer); $('toast').textContent = message; $('toast').className = `toast${error ? ' error' : ''}`; $('toast').hidden = false; toastTimer = setTimeout(() => $('toast').hidden = true, error ? 9000 : 5000); }
async function api(path, body) {
  const response = await fetch(path, { cache: 'no-store', ...(body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
  const data = await response.json(); if (!response.ok) throw new Error(data.error || `请求失败（${response.status}）`); return data;
}
async function action(button, task, success) {
  if (button.disabled) return; const label = button.textContent; button.disabled = true; button.textContent = '处理中…';
  try { await task(); if (success) toast(success); } catch (error) { toast(error.message, true); }
  finally { button.disabled = false; button.textContent = label; }
}
function view(name) {
  document.querySelectorAll('.view').forEach(node => node.hidden = node.id !== name);
  document.querySelectorAll('.navigation [data-view]').forEach(node => { node.classList.toggle('active', node.dataset.view === name); if (node.dataset.view === name) node.setAttribute('aria-current', 'page'); else node.removeAttribute('aria-current'); });
  if (name === 'pets' && !state.petLoaded) loadPets(0);
  if (name === 'messages') loadMessages();
}
document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => view(button.dataset.view)));
function countdown(epoch) {
  if (!epoch) return '重置时间未知'; const minutes = Math.max(0, Math.ceil((epoch * 1000 - Date.now()) / 60000));
  if (!minutes) return '已到重置时间，等待下一次查询';
  if (minutes >= 1440) return `${Math.floor(minutes / 1440)} 天 ${Math.floor(minutes % 1440 / 60)} 小时后重置`;
  return `${minutes >= 60 ? `${Math.floor(minutes / 60)} 小时 ` : ''}${minutes % 60} 分钟后重置`;
}
function windowName(window) { if (window.windowMinutes === 300) return '5 小时窗口'; if (window.windowMinutes === 10080) return '每周窗口'; return `${window.windowMinutes >= 1440 ? `${(window.windowMinutes / 1440).toFixed(1).replace('.0', '')} 天` : `${window.windowMinutes} 分钟`}窗口`; }
function renderWindow(window) {
  return `<div class="window"><div class="window-caption"><strong>${escapeHtml(windowName(window))}</strong><span>已用 ${Number(window.usedPercent).toFixed(1)}%</span></div><progress value="${window.usedPercent}" max="100" aria-label="${escapeHtml(windowName(window))}已用额度"></progress><p class="window-reset">${escapeHtml(countdown(window.resetsAt))}</p></div>`;
}
function renderUsage() {
  if (!state.usage) return;
  $('quota-cards').setAttribute('aria-busy', 'false');
  $('quota-cards').innerHTML = ['codex', 'claude'].map(provider => {
    const data = state.usage[provider]; const name = provider === 'codex' ? 'Codex' : 'Claude'; const windows = [data.primary, data.weekly].filter(Boolean);
    const status = !data.enabled ? '未启用' : data.error ? '查询失败' : data.stale ? '等待更新' : '已更新';
    const head = `<div class="quota-head"><div class="provider-name"><span class="provider-mark" aria-hidden="true">${provider === 'codex' ? '⌘' : '✳'}</span><h2>${name}</h2></div><span class="badge${data.stale && data.enabled ? ' stale' : ''}">${status}</span></div>`;
    if (!data.enabled) return `<article class="quota-card ${provider}">${head}<div class="empty-state"><strong>按需开启。</strong><p class="subtle">在基础设置中启用 ${name}，并填写登录凭据目录。</p><button class="text-button" data-configure>前往设置 →</button></div></article>`;
    const current = windows[0];
    const remaining = current ? (100 - current.usedPercent).toFixed(1).replace('.0', '') : '—';
    return `<article class="quota-card ${provider}">${head}<div class="quota-value"><strong class="quota-number">${remaining}${current ? '<span>%</span>' : ''}</strong><span class="quota-label">${current ? `${escapeHtml(windowName(current))}剩余` : '等待额度数据'}</span></div>${windows.map(renderWindow).join('')}${data.error ? `<p class="quota-error">${escapeHtml(data.error)}${windows.length ? ' 显示的是上次成功数据。' : ''}</p>` : ''}<div class="quota-foot"><span>成功查询 ${data.fetchedAt ? new Date(data.fetchedAt).toLocaleString('zh-CN', { hour12: false }) : '尚无'}</span><span>${data.nextAttemptAt ? `下次查询 ${new Date(data.nextAttemptAt).toLocaleTimeString('zh-CN', { hour12: false })}` : '等待后台查询'}</span></div></article>`;
  }).join('');
  document.querySelectorAll('[data-configure]').forEach(button => button.onclick = () => view('settings'));
}
async function loadUsage() {
  if (polling) return; polling = true;
  try { state.usage = await api('/api/usage'); renderUsage(); $('connection').textContent = '服务已连接'; $('connection').className = 'connection online'; }
  catch (error) { $('connection').textContent = '服务连接中断'; $('connection').className = 'connection error'; if (state.usage) { Object.values(state.usage).forEach(data => data.stale = true); renderUsage(); } }
  finally { polling = false; }
}
$('refresh').onclick = () => action($('refresh'), async () => { state.usage = await api('/api/usage/refresh', {}); renderUsage(); }, '查询完成。频繁点击会沿用缓存，接口限流时自动退避。');
function updateModeHint() {
  const managed = $('codex-mode').value === 'managed';
  $('codex-readonly-fields').hidden = managed; $('codex-managed-fields').hidden = !managed;
  $('codex-home').required = !managed; $('codex-managed-home').required = managed;
  $('mode-hint').textContent = managed ? '服务会在后台自动续期。需要首次登录时，直接在下方控制台启动设备登录。' : '每次查询重新读取只读目录中的 auth.json；此模式不会写入或续期凭据。';
  renderServiceMode($('codex-mode').value);
}
function cleanTerminalText(value) {
  return String(value ?? '').replace(/\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))/g, '').replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, '');
}
function extractDeviceCode(output) {
  const lines = cleanTerminalText(output).replace(/\r\n?/g, '\n').split('\n');
  const promptLine = lines.findIndex(line => /one-time code/i.test(line));
  if (promptLine < 0) return '';
  return lines.slice(promptLine, promptLine + 5).join('\n').match(/\b[A-Z0-9]{4,8}-[A-Z0-9]{4,8}\b/i)?.[0].toUpperCase() ?? '';
}
async function loadCodexLogin() {
  try {
    const data = await api('/api/codex/login');
    const output = cleanTerminalText(data.output);
    $('codex-login-output').textContent = output || (data.error ? data.error : '等待设备登录…');
    $('codex-login-output').scrollTop = $('codex-login-output').scrollHeight;
    codexLoginCode = extractDeviceCode(output);
    $('codex-login-copy-code').hidden = !codexLoginCode;
    const foundUrl = output.match(/https:\/\/auth\.openai\.com\/[^\s"'<>]+/);
    const loginUrl = foundUrl?.[0].replace(/[),.;]+$/, '');
    $('codex-login-link').hidden = !loginUrl;
    if (loginUrl) $('codex-login-link').href = loginUrl;
    const succeeded = Boolean(data.authenticated);
    $('codex-login-console').hidden = succeeded;
    $('codex-login-success').hidden = !succeeded;
    $('codex-login-status').textContent = data.running ? '等待完成设备验证' : succeeded ? '已检测到登录凭据' : data.startedAt ? data.exitCode === 0 ? '未检测到登录凭据' : `登录进程已结束${data.exitCode == null ? '' : ` · 退出码 ${data.exitCode}`}` : '尚未登录';
    $('codex-login-start').disabled = Boolean(data.running);
    $('codex-login-cancel').disabled = !data.running;
    if (!data.running && data.startedAt && data.exitCode === 0 && loginRefreshedAt !== data.startedAt) {
      loginRefreshedAt = data.startedAt;
      sessionStorage.setItem('codex-login-refreshed-at', data.startedAt);
      try { state.usage = await api('/api/codex/login/refresh', {}); renderUsage(); toast('登录完成，已刷新 Codex 额度。'); }
      catch (error) { toast(`登录完成，但额度刷新失败：${error.message}`, true); }
    }
  } catch { /* Login status is unavailable until the service is reachable. */ }
}
async function copyLoginCode() {
  if (!codexLoginCode) return;
  try {
    if (!navigator.clipboard?.writeText) throw new Error('剪贴板不可用');
    await navigator.clipboard.writeText(codexLoginCode);
    const dialog = $('codex-code-dialog');
    if (dialog.open && typeof dialog.close === 'function') dialog.close();
    else dialog.removeAttribute('open');
    toast('验证码已复制。');
  } catch {
    const dialog = $('codex-code-dialog'), input = $('codex-code-manual');
    input.value = codexLoginCode;
    if (!dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    }
    input.focus(); input.select();
    input.setSelectionRange(0, input.value.length);
  }
}
async function startCodexLogin() {
  $('codex-login-console').hidden = false;
  $('codex-login-success').hidden = true;
  $('codex-login-start').disabled = true;
  try { await api('/api/codex/login/start', {}); await loadCodexLogin(); toast('设备登录已启动，请按控制台提示完成验证。'); }
  catch (error) { toast(error.message, true); await loadCodexLogin(); }
}
function renderServiceMode(mode) {
  $('service-mode').textContent = `Codex 模式 · ${mode}`;
  $('service-mode').classList.toggle('managed', mode === 'managed');
}
function deviceList() { return state.settings?.devices ?? []; }
function renderDeviceTargets() {
  const devices = deviceList();
  if (!devices.some(device => device.id === state.deviceTargetId)) state.deviceTargetId = devices[0]?.id ?? '';
  for (const id of ['device-target', 'pet-device']) {
    const select = $(id);
    select.replaceChildren(...(devices.length ? devices.map(device => new Option(`${device.name} · ${device.host}`, device.id)) : [new Option('请先添加设备', '')]));
    select.value = state.deviceTargetId;
    select.disabled = devices.length === 0;
  }
  for (const id of ['pet-reset']) $(id).disabled = devices.length === 0;
  $('pet-apply').disabled = !state.selected || !devices.length || $('pet-preview').hidden;
  renderDeviceControls();
}
function renderDeviceControls() {
  const device = deviceList().find(entry => entry.id === state.deviceTargetId);
  const active = Boolean(device);
  $('device-config-empty').hidden = active;
  $('device-setting-list').hidden = !active;
  $('set-display').disabled = !active;
  $('set-reset-time').disabled = !active;
  $('set-brightness').disabled = !active;
  $('save-device-schedule').disabled = !active;
  $('device-setting-list').querySelectorAll('input, select').forEach(field => field.disabled = !active);
  if (!device) return;
  const info = state.deviceInfos[device.id];
  $('reset-time-format').value = info?.reset_time_format === 'datetime' ? 'datetime' : 'countdown';
  $('display-mode').value = ['auto', 'codex', 'claude', 'dual'].includes(info?.mode) ? info.mode : 'auto';
  $('brightness').value = info?.brightness ?? device.dayBrightness ?? 100;
  $('page-schedule-enabled').checked = Boolean(device.pageScheduleEnabled);
  $('page-mode').value = device.pageMode ?? 'auto';
  $('page-on-time').value = device.pageOnTime ?? '08:00';
  $('page-off-time').value = device.pageOffTime ?? '22:00';
  $('page-off-brightness').value = device.pageOffBrightness ?? 0;
  $('night-mode-enabled').checked = Boolean(device.nightModeEnabled);
  $('night-start').value = device.nightStart ?? '22:00';
  $('night-end').value = device.nightEnd ?? '07:00';
  $('night-brightness').value = device.nightBrightness ?? 0;
  for (const [inputId, outputId] of [['brightness', 'brightness-value'], ['page-off-brightness', 'page-off-brightness-value'], ['night-brightness', 'night-brightness-value']]) {
    $(outputId).value = `${$(inputId).value}%`; $(outputId).textContent = $(outputId).value;
  }
}
function renderDeviceList() {
  const devices = deviceList();
  if (!devices.length) { $('device-list').innerHTML = '<p class="empty-devices">还没有添加小屏幕。填写设备名称和 IP 后即可测试连接并配对。</p>'; return; }
  $('device-list').innerHTML = devices.map((device, index) => `<article class="device-row" data-device-index="${index}">
    <div class="device-row-fields"><label>设备名称<input data-device-field="name" maxlength="50" required></label><label>IP 地址<input data-device-field="host" autocomplete="off" required></label></div>
    <div class="device-row-actions"><span class="device-status" data-device-status>${escapeHtml(state.deviceStatuses[device.id] ?? '尚未测试连接')}</span><button class="button" data-device-action="test">测试连接</button><button class="button" data-device-action="pair">配对</button><button class="button" data-device-action="save">保存信息</button><button class="text-button danger-button" data-device-action="delete">移除</button><a class="button device-page-link" data-device-page target="_blank" rel="noopener noreferrer">打开屏幕页面 ↗</a></div>
  </article>`).join('');
  document.querySelectorAll('.device-row').forEach(row => {
    const device = devices[Number(row.dataset.deviceIndex)];
    row.querySelector('[data-device-field="name"]').value = device.name;
    row.querySelector('[data-device-field="host"]').value = device.host;
    const pageLink = row.querySelector('[data-device-page]');
    pageLink.href = new URL(`http://${device.host}/`).href;
    const route = action => `/api/devices/${encodeURIComponent(device.id)}/${action}`;
    row.querySelector('[data-device-action="test"]').onclick = buttonEvent => action(buttonEvent.currentTarget, async () => {
      const info = await api(route('info'));
      state.deviceInfos[device.id] = info; state.deviceTargetId = device.id; renderDeviceTargets();
      const status = `已连接 · ${info.ip || device.host}${info.fw ? ` · ${info.fw}` : ''}`;
      if (Number.isFinite(info.brightness)) {
        state.settings.devices = state.settings.devices.map(entry => entry.id === device.id ? { ...entry, dayBrightness: info.brightness } : entry);
        renderDeviceControls();
      }
      state.deviceStatuses[device.id] = status; row.querySelector('[data-device-status]').textContent = status;
    }, '设备连接正常。');
    row.querySelector('[data-device-action="pair"]').onclick = buttonEvent => action(buttonEvent.currentTarget, () => api(route('pair'), {}), `“${device.name}”已指向本服务。`);
    row.querySelector('[data-device-action="save"]').onclick = buttonEvent => action(buttonEvent.currentTarget, async () => {
      await api(route('update'), { name: row.querySelector('[data-device-field="name"]').value.trim(), host: row.querySelector('[data-device-field="host"]').value.trim() });
      delete state.deviceStatuses[device.id]; delete state.deviceInfos[device.id]; await loadSettings();
    }, '设备信息已保存。');
    row.querySelector('[data-device-action="delete"]').onclick = buttonEvent => action(buttonEvent.currentTarget, async () => {
      await api(route('delete'), {}); delete state.deviceStatuses[device.id]; delete state.deviceInfos[device.id]; await loadSettings();
    }, `“${device.name}”已移除。`);
  });
}
function renderDevices() { renderDeviceTargets(); renderDeviceList(); }
async function loadSettings() {
  const data = await api('/api/settings'); state.settings = data.settings; state.runtime = data.runtime; const s = data.settings;
  $('advertised-host').value = s.advertisedHost; $('port').value = data.runtime.portFromEnvironment || data.runtime.docker ? data.runtime.port : s.port;
  $('refresh-minutes').value = Number((s.refreshSeconds / 60).toFixed(2)); $('codex-enabled').checked = s.codexEnabled; $('claude-enabled').checked = s.claudeEnabled;
  $('codex-home').value = s.codexHome; $('codex-managed-home').value = s.codexManagedHome; $('claude-home').value = s.claudeHome; $('codex-mode').value = s.codexMode;
  renderServiceMode(s.codexMode);
  $('port').disabled = data.runtime.portFromEnvironment || data.runtime.docker;
  $('port-hint').textContent = data.runtime.docker ? `容器内部监听 ${data.runtime.port}。对外端口在 Compose 的 AICLOCK_HOST_PORT 中修改，然后重新创建容器。`
    : data.runtime.portFromEnvironment ? `当前由 AICLOCK_PORT 指定监听 ${data.runtime.port}，环境变量优先于网页设置。` : `当前监听 ${data.runtime.port}。修改端口后重启服务，并同步设备可访问的服务地址。`;
  updateModeHint(); renderDevices();
}
$('codex-mode').onchange = updateModeHint;
$('codex-login-start').onclick = startCodexLogin;
$('codex-login-again').onclick = startCodexLogin;
$('codex-login-copy-code').onclick = copyLoginCode;
$('codex-code-retry').onclick = copyLoginCode;
$('codex-login-cancel').onclick = async () => {
  $('codex-login-cancel').disabled = true;
  try { await api('/api/codex/login/cancel', {}); await loadCodexLogin(); toast('登录流程已取消。'); }
  catch (error) { toast(error.message, true); await loadCodexLogin(); }
};
setInterval(() => { if ($('codex-mode').value === 'managed') loadCodexLogin(); }, 1200);
$('settings-form').onsubmit = event => { event.preventDefault(); action($('save-settings'), async () => {
  const result = await api('/api/settings', { port: $('port').disabled ? state.settings.port : Number($('port').value), deviceHost: '', devices: state.settings.devices, advertisedHost: $('advertised-host').value.trim(), refreshSeconds: Math.round(Number($('refresh-minutes').value) * 60), codexEnabled: $('codex-enabled').checked, claudeEnabled: $('claude-enabled').checked, codexMode: $('codex-mode').value, codexHome: $('codex-home').value.trim(), codexManagedHome: $('codex-managed-home').value.trim(), claudeHome: $('claude-home').value.trim(), messageSchedule: messagesState?.schedule ?? state.settings.messageSchedule });
  await loadSettings(); await loadUsage(); await loadMessages(); toast(result.restartRequired ? '设置已保存。监听端口需重启服务后生效，请同步更新设备服务地址。' : '设置已保存，后台将按刷新间隔查询。');
}); };
function selectedDeviceId() { if (!state.deviceTargetId) throw new Error('请先添加并选择一台小屏幕。'); return state.deviceTargetId; }
$('device-add-form').onsubmit = event => { event.preventDefault(); action($('add-device'), async () => {
  const entry = await api('/api/devices', { name: $('new-device-name').value.trim(), host: $('new-device-host').value.trim() });
  state.deviceTargetId = entry.id; $('new-device-name').value = ''; $('new-device-host').value = ''; await loadSettings();
}, '小屏幕已添加。'); };
$('device-target').onchange = () => { state.deviceTargetId = $('device-target').value; renderDeviceTargets(); };
$('set-reset-time').onclick = () => action($('set-reset-time'), async () => {
  const id = selectedDeviceId(), format = $('reset-time-format').value;
  await api(`/api/devices/${encodeURIComponent(id)}/reset-time`, { format });
  state.deviceInfos[id] = { ...state.deviceInfos[id], reset_time_format: format };
}, '重置时间显示方式已写入设备，重启后保留。');
$('pet-device').onchange = () => { state.deviceTargetId = $('pet-device').value; renderDeviceTargets(); };
$('set-display').onclick = () => action($('set-display'), async () => {
  const id = selectedDeviceId(), mode = $('display-mode').value;
  await api(`/api/devices/${encodeURIComponent(id)}/display`, { mode });
  state.deviceInfos[id] = { ...state.deviceInfos[id], mode };
}, '显示模式已保存到所选设备。');
for (const [inputId, outputId] of [['brightness', 'brightness-value'], ['page-off-brightness', 'page-off-brightness-value'], ['night-brightness', 'night-brightness-value']]) {
  $(inputId).oninput = () => { $(outputId).value = `${$(inputId).value}%`; $(outputId).textContent = $(outputId).value; };
}
$('set-brightness').onclick = () => action($('set-brightness'), async () => {
  const id = selectedDeviceId(); const level = Number($('brightness').value);
  await api(`/api/devices/${encodeURIComponent(id)}/brightness`, { level });
  state.settings.devices = state.settings.devices.map(device => device.id === id ? { ...device, dayBrightness: level } : device);
  state.deviceInfos[id] = { ...state.deviceInfos[id], brightness: level };
}, '常规亮度已保存到所选设备。');
$('save-device-schedule').onclick = () => action($('save-device-schedule'), async () => {
  const id = selectedDeviceId();
  await api(`/api/devices/${encodeURIComponent(id)}/schedule`, {
    pageScheduleEnabled: $('page-schedule-enabled').checked, pageMode: $('page-mode').value,
    pageOnTime: $('page-on-time').value, pageOffTime: $('page-off-time').value, pageOffBrightness: Number($('page-off-brightness').value),
    nightModeEnabled: $('night-mode-enabled').checked, nightStart: $('night-start').value,
    nightEnd: $('night-end').value, nightBrightness: Number($('night-brightness').value)
  });
  await loadSettings();
}, '定时页面与夜间亮度设置已保存。');
async function loadPets(page) {
  const sequence = ++petSearchSequence; $('pet-list').innerHTML = '<p class="loading-message">正在加载 petdex 图库…</p>';
  try {
    const data = await api(`/api/pets?search=${encodeURIComponent($('pet-search').value.trim())}&page=${page}`); if (sequence !== petSearchSequence) return;
    state.petLoaded = true; state.pets = data.pets; state.page = data.page; state.total = data.total;
    $('pet-count').textContent = `找到 ${data.total.toLocaleString('zh-CN')} 位伙伴`;
    $('pet-page').textContent = `${data.total ? data.page + 1 : 0} / ${Math.ceil(data.total / data.pageSize)}`;
    $('pet-prev').disabled = data.page === 0; $('pet-next').disabled = (data.page + 1) * data.pageSize >= data.total;
    const animation = $('pet-animation').value; $('pet-animation').replaceChildren(...data.animations.map(a => new Option(a.label, a.id))); if (data.animations.some(a => a.id === animation)) $('pet-animation').value = animation;
    $('pet-list').innerHTML = data.pets.length ? data.pets.map((pet, index) => `<button class="pet-item${state.selected?.slug === pet.slug ? ' selected' : ''}" data-index="${index}"><span class="pet-index">${String(data.page * data.pageSize + index + 1).padStart(2, '0')}</span><span><strong>${escapeHtml(pet.displayName)}</strong><small>${escapeHtml(pet.slug)}</small></span><span class="pet-kind">${escapeHtml(pet.kind)}</span></button>`).join('') : '<p class="loading-message">没有找到匹配的宠物，试试其他关键词。</p>';
    document.querySelectorAll('.pet-item').forEach(button => button.onclick = () => { state.selected = state.pets[Number(button.dataset.index)]; document.querySelectorAll('.pet-item').forEach(node => node.classList.toggle('selected', node === button)); preview(); });
  } catch (error) { if (sequence === petSearchSequence) { $('pet-list').innerHTML = `<p class="page-error">${escapeHtml(error.message)}</p>`; toast(error.message, true); } }
}
function preview() {
  if (!state.selected) return; const sequence = ++previewSequence; $('selected-pet').textContent = state.selected.displayName; $('selected-slug').textContent = state.selected.slug;
  $('preview-message').textContent = '正在准备动画…'; $('preview-message').hidden = false; $('pet-preview').hidden = true; $('pet-apply').disabled = true;
  const image = new Image(); image.onload = () => { if (sequence !== previewSequence) return; $('pet-preview').src = image.src; $('pet-preview').hidden = false; $('preview-message').hidden = true; $('pet-apply').disabled = !state.deviceTargetId; };
  image.onerror = () => { if (sequence !== previewSequence) return; $('preview-message').textContent = '预览加载失败，请重新选择或稍后重试。'; };
  image.src = `/api/pets/${encodeURIComponent(state.selected.slug)}/preview?animation=${encodeURIComponent($('pet-animation').value)}&slot=${$('pet-slot').value}`;
}
$('pet-slot').onchange = preview; $('pet-animation').onchange = preview;
$('pet-search-button').onclick = () => loadPets(0);
$('pet-search').onkeydown = event => { if (event.key === 'Enter') { event.preventDefault(); loadPets(0); } };
$('pet-prev').onclick = () => loadPets(state.page - 1); $('pet-next').onclick = () => loadPets(state.page + 1);
$('pet-apply').onclick = () => { if (!state.selected) return; action($('pet-apply'), () => api(`/api/pets/${encodeURIComponent(state.selected.slug)}/apply`, { deviceId: selectedDeviceId(), slot: $('pet-slot').value, animation: $('pet-animation').value }), '宠物已上传并保存到所选小屏幕。'); };
$('pet-reset').onclick = () => action($('pet-reset'), () => api('/api/pets/reset', { deviceId: selectedDeviceId(), slot: $('pet-slot').value }), '已在所选小屏幕恢复该插槽的默认宠物。');
function messageDate(value) { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'; }
function renderMessages(data) {
  messagesState = data;
  const mode = data.codexMode ?? state.settings?.codexMode;
  if (mode) renderServiceMode(mode);
  if (state.settings) state.settings.messageSchedule = data.schedule;
  if (!messageFormLoaded) {
    $('message-enabled').checked = data.schedule.enabled; $('message-time').value = data.schedule.time;
    $('message-text').value = data.schedule.message;
    if (data.schedule.model) $('message-model').replaceChildren(new Option(data.schedule.model, data.schedule.model));
    messageFormLoaded = true;
  }
  $('message-plan-status').textContent = data.running ? '正在发送' : data.schedule.enabled ? '每日发送已开启' : '定时发送未开启';
  $('message-timezone').textContent = `服务时区：${data.timeZone} · 当前时间 ${data.serverTime.slice(0, 16).replace('T', ' ')}`;
  $('message-next').textContent = data.nextRunAt ? `下次计划：${data.nextRunAt.slice(0, 16).replace('T', ' ')}（服务时间）` : '定时发送未开启';
  const unavailable = mode === 'readonly' ? '当前为 readonly 模式，仅查询额度，无法加载模型或发送消息。请在基础设置启用 Codex，并切换到 managed（自动续期）。'
    : data.codexEnabled === false ? '当前为 managed 模式，但 Codex 未启用。请在基础设置启用 Codex 后加载模型。'
    : '模型加载与发送需要启用 Codex，并使用 managed（自动续期）模式。';
  $('message-availability').textContent = data.canSend ? '' : unavailable;
  $('message-configure').hidden = data.canSend;
  $('message-model').disabled = !data.canSend;
  if (!$('message-model').value && $('message-model').options.length === 1) $('message-model').options[0].textContent = data.canSend ? '请加载账号可用模型' : mode === 'readonly' ? 'readonly 模式暂不支持模型加载' : '请先启用 Codex';
  $('message-mode-hint').textContent = data.canSend ? '发送将使用自动续期目录中登录的订阅账号。若加载失败，请检查登录状态。' : 'Docker 统一部署已包含 Codex CLI；请在基础设置启用 Codex 并选择自动续期，然后从页面完成登录。';
  $('message-models').disabled = !data.canSend;
  $('message-send').disabled = messageSending || data.running || !data.canSend || !data.schedule.model;
  $('message-runs').innerHTML = data.runs.length ? data.runs.map(run => {
    const status = { running: '发送中', succeeded: '已完成', failed: '失败', interrupted: '已中断' }[run.status] ?? run.status;
    const window = run.primary?.windowMinutes === 300 ? run.primary : null;
    return `<article class="message-run ${run.status}"><div class="message-run-head"><strong>${escapeHtml(run.model)}</strong><span class="badge">${escapeHtml(status)}</span></div><p class="subtle">${run.source === 'scheduled' ? '定时发送' : '立即发送'} · ${escapeHtml(messageDate(run.startedAt))}</p><p class="message-prompt">${escapeHtml(run.message)}</p>${run.reply ? `<pre class="message-reply">${escapeHtml(run.reply)}</pre>` : ''}${run.error ? `<p class="quota-error">${escapeHtml(run.error)}</p>` : ''}${run.quotaError ? `<p class="quota-error">${escapeHtml(run.quotaError)}</p>` : ''}${run.status === 'succeeded' && !run.quotaError ? `<p class="message-window">${window ? `发送后 5 小时窗口：已用 ${Number(window.usedPercent).toFixed(1)}% · 重置于 ${escapeHtml(messageDate(window.resetsAt * 1000))}` : '发送后的额度响应未包含 5 小时窗口，等待后续额度查询。'}</p>` : ''}${run.finishedAt ? `<p class="subtle">结束 ${escapeHtml(messageDate(run.finishedAt))}</p>` : '<p class="subtle">等待模型回复，最长 3 分钟…</p>'}</article>`;
  }).join('') : '<div class="empty-state"><strong>还没有发送记录。</strong><p class="subtle">保存模型与消息后，可立即发送验证回复；也可开启每日定时发送。</p></div>';
}
async function loadMessages() {
  if (messagePolling) return; messagePolling = true;
  try { renderMessages(await api('/api/messages')); }
  catch (error) { $('message-plan-status').textContent = '消息服务连接中断'; }
  finally { messagePolling = false; }
}
$('message-models').onclick = () => action($('message-models'), async () => {
  try {
    const models = await api('/api/messages/models'); const selected = $('message-model').value;
    $('message-model').replaceChildren(...models.map(model => new Option(model.name, model.id)));
    if (models.some(model => model.id === selected)) $('message-model').value = selected;
    else if (models.some(model => model.isDefault)) $('message-model').value = models.find(model => model.isDefault).id;
    if (!models.length) $('message-model').replaceChildren(new Option('账号没有返回可用文本模型', ''));
    $('message-model-error').textContent = models.length ? `已加载 ${models.length} 个模型。选择后保存计划。` : '请检查登录账号与 Codex CLI 版本。';
  } catch (error) { $('message-model-error').textContent = error.message; throw error; }
});
$('message-form').onsubmit = event => { event.preventDefault(); action($('message-save'), async () => {
  await api('/api/messages/schedule', { enabled: $('message-enabled').checked, time: $('message-time').value, model: $('message-model').value, message: $('message-text').value.trim() });
  await loadMessages();
}, '发送计划已保存。'); };
$('message-send').onclick = () => action($('message-send'), async () => {
  messageSending = true;
  try {
    const run = await api('/api/messages/send', {});
    if (run?.status !== 'succeeded') throw new Error(run?.error || '发送未完成，请查看最近结果。');
    toast('消息已完成，回复与发送后的窗口显示在最近结果。');
  } finally { messageSending = false; await loadMessages(); await loadUsage(); }
}).finally(() => { if (messagesState) $('message-send').disabled = messagesState.running || !messagesState.canSend || !messagesState.schedule.model; });
Promise.all([loadSettings(), loadUsage(), loadMessages()]).catch(error => toast(error.message, true));
setInterval(loadUsage, 5000);
setInterval(loadMessages, 5000);
