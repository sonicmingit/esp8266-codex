'use strict';
const $ = id => document.getElementById(id);
const state = { usage: null, settings: null, runtime: null, pets: [], selected: null, page: 0, total: 0, petLoaded: false };
const escapeHtml = value => { const node = document.createElement('span'); node.textContent = String(value ?? ''); return node.innerHTML; };
let toastTimer, previewSequence = 0, petSearchSequence = 0, polling = false;
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
function updateModeHint() { $('mode-hint').textContent = $('codex-mode').value === 'managed' ? '需要已安装 Codex CLI，以及独立、可写且已登录的服务目录。请勿与正在使用的宿主机凭据共用。登录步骤见中文说明。' : '每次查询重新读取 auth.json。宿主机更新凭据后自动生效；此模式不会写入或续期凭据。'; }
async function loadSettings() {
  const data = await api('/api/settings'); state.settings = data.settings; state.runtime = data.runtime; const s = data.settings;
  $('device-host').value = s.deviceHost; $('advertised-host').value = s.advertisedHost; $('port').value = data.runtime.portFromEnvironment || data.runtime.docker ? data.runtime.port : s.port;
  $('refresh-seconds').value = s.refreshSeconds; $('codex-enabled').checked = s.codexEnabled; $('claude-enabled').checked = s.claudeEnabled;
  $('codex-home').value = s.codexHome; $('claude-home').value = s.claudeHome; $('codex-mode').value = s.codexMode;
  $('port').disabled = data.runtime.portFromEnvironment || data.runtime.docker;
  $('port-hint').textContent = data.runtime.docker ? `容器内部监听 ${data.runtime.port}。对外端口在 Compose 的 AICLOCK_HOST_PORT 中修改，然后重新创建容器。`
    : data.runtime.portFromEnvironment ? `当前由 AICLOCK_PORT 指定监听 ${data.runtime.port}，环境变量优先于网页设置。` : `当前监听 ${data.runtime.port}。修改端口后重启服务，并同步设备可访问的服务地址。`;
  updateModeHint();
}
$('codex-mode').onchange = updateModeHint;
$('settings-form').onsubmit = event => { event.preventDefault(); action($('save-settings'), async () => {
  const result = await api('/api/settings', { port: $('port').disabled ? state.settings.port : Number($('port').value), deviceHost: $('device-host').value.trim(), advertisedHost: $('advertised-host').value.trim(), refreshSeconds: Number($('refresh-seconds').value), codexEnabled: $('codex-enabled').checked, claudeEnabled: $('claude-enabled').checked, codexMode: $('codex-mode').value, codexHome: $('codex-home').value.trim(), claudeHome: $('claude-home').value.trim() });
  await loadSettings(); await loadUsage(); toast(result.restartRequired ? '设置已保存。监听端口需重启服务后生效，请同步更新设备服务地址。' : '设置已保存，后台将按刷新间隔查询。');
}); };
$('device-test').onclick = () => action($('device-test'), async () => { const info = await api('/api/device'); $('device-status').textContent = `已连接 · ${info.ip || state.settings.deviceHost}${info.fw ? ` · ${info.fw}` : ''}`;
  if (['auto','codex','claude','dual'].includes(info.mode)) $('display-mode').value = info.mode;
  if (Number.isFinite(info.brightness)) { $('brightness').value = info.brightness; $('brightness-value').textContent = `${info.brightness}%`; }
}, '设备连接正常。');
$('device-pair').onclick = () => action($('device-pair'), () => api('/api/device/pair', {}), '已将设备桥接地址设为本服务。');
$('set-display').onclick = () => action($('set-display'), () => api('/api/device/display', { mode: $('display-mode').value }), '显示模式已保存到设备。');
$('brightness').oninput = () => $('brightness-value').textContent = `${$('brightness').value}%`;
$('set-brightness').onclick = () => action($('set-brightness'), () => api('/api/device/brightness', { level: Number($('brightness').value) }), '亮度已保存到设备。');
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
  const image = new Image(); image.onload = () => { if (sequence !== previewSequence) return; $('pet-preview').src = image.src; $('pet-preview').hidden = false; $('preview-message').hidden = true; $('pet-apply').disabled = false; };
  image.onerror = () => { if (sequence !== previewSequence) return; $('preview-message').textContent = '预览加载失败，请重新选择或稍后重试。'; };
  image.src = `/api/pets/${encodeURIComponent(state.selected.slug)}/preview?animation=${encodeURIComponent($('pet-animation').value)}&slot=${$('pet-slot').value}`;
}
$('pet-slot').onchange = preview; $('pet-animation').onchange = preview;
$('pet-search-button').onclick = () => loadPets(0);
$('pet-search').onkeydown = event => { if (event.key === 'Enter') { event.preventDefault(); loadPets(0); } };
$('pet-prev').onclick = () => loadPets(state.page - 1); $('pet-next').onclick = () => loadPets(state.page + 1);
$('pet-apply').onclick = () => { if (!state.selected) return; action($('pet-apply'), () => api(`/api/pets/${encodeURIComponent(state.selected.slug)}/apply`, { slot: $('pet-slot').value, animation: $('pet-animation').value }), '宠物已上传并保存到小屏幕。'); };
$('pet-reset').onclick = () => action($('pet-reset'), () => api('/api/pets/reset', { slot: $('pet-slot').value }), '已恢复该插槽的默认宠物。');
Promise.all([loadSettings(), loadUsage()]).catch(error => toast(error.message, true));
setInterval(loadUsage, 5000);
