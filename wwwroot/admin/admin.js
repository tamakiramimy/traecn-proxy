const { createApp, computed, ref } = Vue;

createApp({
  setup() {
    const adminKey = ref(sessionStorage.getItem('trancnAdminKey') || '');
    const accounts = ref([]);
    const settings = ref({ load_balancing: 'priority', session_ttl_minutes: 60 });
    const importJson = ref('');
    const loginAlias = ref('');
    const connected = ref(false);
    const busy = ref(new Set());
    const notice = ref(null);
    const showKey = ref(false);
    let noticeTimer;

    const enabledCount = computed(() => accounts.value.filter(account => account.enabled).length);
    const disabledCount = computed(() => accounts.value.length - enabledCount.value);
    const strategyLabel = computed(() => settings.value.load_balancing === 'balanced' ? '并发均衡' : '优先级');
    const isBusy = key => busy.value.has(key);

    const setBusy = (key, active) => {
      const next = new Set(busy.value);
      active ? next.add(key) : next.delete(key);
      busy.value = next;
    };

    const showNotice = (text, type = 'success') => {
      window.clearTimeout(noticeTimer);
      notice.value = { text: normalizeError(text), type };
      noticeTimer = window.setTimeout(() => { notice.value = null; }, 4800);
    };

    const normalizeError = value => {
      if (typeof value !== 'string') return '请求失败，请稍后重试。';
      try {
        const payload = JSON.parse(value);
        return payload.error?.message || payload.error || value;
      } catch { return value; }
    };

    const api = async (path, options = {}) => {
      const response = await fetch(path, {
        ...options,
        headers: {
          Authorization: `Bearer ${adminKey.value}`,
          'Content-Type': 'application/json',
          ...(options.headers || {})
        }
      });
      if (!response.ok) {
        const payload = await response.text();
        throw new Error(normalizeError(payload) || `请求失败 (${response.status})`);
      }
      return response.status === 204 ? null : response.json();
    };

    const runAction = async (key, action, successText) => {
      if (isBusy(key)) return null;
      setBusy(key, true);
      try {
        const result = await action();
        if (successText) showNotice(typeof successText === 'function' ? successText(result) : successText);
        return result;
      } catch (error) {
        showNotice(error.message || '操作失败，请稍后重试。', 'error');
        return null;
      } finally { setBusy(key, false); }
    };

    const fetchAccounts = async () => {
      const data = await api('/admin/api/accounts');
      accounts.value = data.accounts || [];
      settings.value = data.settings || settings.value;
      connected.value = true;
      sessionStorage.setItem('trancnAdminKey', adminKey.value);
      return data;
    };

    const loadAccounts = async (key = 'connect') => {
      const data = await runAction(key, fetchAccounts, key === 'refresh' ? '账号数据已刷新' : null);
      if (!data && key === 'connect') {
        connected.value = false;
        sessionStorage.removeItem('trancnAdminKey');
      }
      return data;
    };

    const connect = () => loadAccounts('connect');
    const toggleAccount = account => runAction(`toggle:${account.alias}`, async () => {
      await api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/${account.enabled ? 'disable' : 'enable'}`, { method: 'POST' });
      await fetchAccounts();
    }, `${account.alias} 已${account.enabled ? '停用' : '启用'}`);
    const refreshAccount = account => runAction(`refresh:${account.alias}`, async () => {
      await api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/refresh`, { method: 'POST' });
      await fetchAccounts();
    }, `${account.alias} Token 刷新成功`);
    const testAccount = account => runAction(`test:${account.alias}`, () =>
      api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/test`, { method: 'POST' }), `${account.alias} Token 校验通过`);
    const removeAccount = account => {
      if (!window.confirm(`确认删除账号“${account.alias}”？\n\n删除后，该账号将立即停止接收请求。`)) return;
      return runAction(`remove:${account.alias}`, async () => {
        await api(`/admin/api/accounts/${encodeURIComponent(account.alias)}`, { method: 'DELETE' });
        await fetchAccounts();
      }, `${account.alias} 已删除`);
    };
    const setPriority = account => runAction(`priority:${account.alias}`, () =>
      api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/priority/${account.priority}`, { method: 'POST' }), `${account.alias} 优先级已更新`);
    const saveSettings = () => runAction('settings', () =>
      api('/admin/api/settings', { method: 'PUT', body: JSON.stringify(settings.value) }), '调度设置已保存');
    const importAccounts = () => {
      if (!window.confirm('导入会完整替换当前账号集合。\n\n建议先备份 accounts.json，是否继续？')) return;
      return runAction('import', async () => {
        await api('/admin/api/accounts/import', { method: 'POST', body: importJson.value });
        importJson.value = '';
        await fetchAccounts();
      }, '账号已导入并替换');
    };
    const startLogin = async () => {
      if (isBusy('login')) return;
      // 在用户点击的同步调用栈内创建窗口，避免异步请求后被浏览器判定为弹窗。
      const popup = window.open('', '_blank');
      if (!popup) {
        showNotice('浏览器阻止了新窗口，请允许弹出窗口后重试。', 'error');
        return;
      }
      popup.opener = null;
      setBusy('login', true);
      try {
        const data = await api('/admin/api/accounts/login/start', { method: 'POST', body: JSON.stringify({ alias: loginAlias.value }) });
        popup.location.replace(data.authorization_url);
        showNotice('已打开 Trae 授权页，完成后请刷新账号池。');
      } catch (error) {
        popup.close();
        showNotice(error.message || '创建授权失败，请稍后重试。', 'error');
      } finally { setBusy('login', false); }
    };

    const formatDate = value => value ? new Date(value).toLocaleString('zh-CN', {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false
    }) : '未设置';
    const relativeExpiry = value => {
      if (!value) return '无到期信息';
      const minutes = Math.round((new Date(value).getTime() - Date.now()) / 60000);
      if (minutes <= 0) return '已过期';
      if (minutes < 60) return `${minutes} 分钟后`;
      const hours = Math.round(minutes / 60);
      if (hours < 48) return `${hours} 小时后`;
      return `${Math.round(hours / 24)} 天后`;
    };
    const tokenState = account => {
      if (!account.token_expires) return { label: '可用', className: 'success' };
      const remaining = new Date(account.token_expires).getTime() - Date.now();
      if (remaining <= 0) return { label: 'Token 过期', className: 'danger' };
      if (remaining < 3600000) return { label: '即将过期', className: 'warning' };
      return { label: '运行中', className: 'success' };
    };
    const initials = alias => (alias || '?').slice(0, 2).toUpperCase();

    if (adminKey.value) loadAccounts('connect');

    return {
      adminKey, accounts, settings, importJson, loginAlias, connected, notice, showKey,
      enabledCount, disabledCount, strategyLabel, isBusy, connect, loadAccounts, toggleAccount,
      refreshAccount, testAccount, removeAccount, setPriority, saveSettings, importAccounts,
      startLogin, formatDate, relativeExpiry, tokenState, initials
    };
  }
}).mount('#app');
