const { createApp, computed, ref } = Vue;

createApp({
  setup() {
    const adminKey = ref(sessionStorage.getItem('trancnAdminKey') || '');
    const accounts = ref([]);
    const settings = ref({ load_balancing: 'priority', session_ttl_minutes: 60, default_max_concurrency: 10 });
    const importJson = ref('');
    const loginAlias = ref('');
    const loginMaxConcurrency = ref(10);
    const connected = ref(false);
    const busy = ref(new Set());
    const notice = ref(null);
    const showKey = ref(false);
    const modelTest = ref({ open: false, alias: '', models: [], selected: '', result: null });
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
      loginMaxConcurrency.value = settings.value.default_max_concurrency || 10;
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
    const setMaxConcurrency = account => runAction(`concurrency:${account.alias}`, async () => {
      try {
        await api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/max-concurrency/${account.max_concurrency}`, { method: 'POST' });
      } finally { await fetchAccounts(); }
    }, `${account.alias} 最大并发已更新`);

    const configNameOf = modelId =>
      modelTest.value.models.find(model => model.id === modelId)?.config_name || modelId;
    const closeModelTest = () => { modelTest.value = { open: false, alias: '', models: [], selected: '', result: null }; };
    const openModelTest = account => runAction(`models:${account.alias}`, async () => {
      const data = await api(`/admin/api/accounts/${encodeURIComponent(account.alias)}/models`);
      const models = data.models || [];
      modelTest.value = { open: true, alias: account.alias, models, selected: models[0]?.id || '', result: null };
    });
    const runModelTest = () => runAction('model-test', async () => {
      const { alias, selected } = modelTest.value;
      try {
        const data = await api(`/admin/api/accounts/${encodeURIComponent(alias)}/models/test`, {
          method: 'POST',
          body: JSON.stringify({ model: selected })
        });
        modelTest.value = { ...modelTest.value, result: { ...data, ok: true } };
      } catch (error) {
        modelTest.value = { ...modelTest.value, result: { ok: false, error: error.message } };
      }
    });

    const saveSettings = () => runAction('settings', async () => {
      await api('/admin/api/settings', { method: 'PUT', body: JSON.stringify(settings.value) });
      loginMaxConcurrency.value = settings.value.default_max_concurrency;
    }, '调度设置已保存');
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
      const maxConcurrency = Number(loginMaxConcurrency.value);
      if (!Number.isInteger(maxConcurrency) || maxConcurrency < 1 || maxConcurrency > 100) {
        showNotice('最大并发必须是 1 到 100 之间的整数。', 'error');
        return;
      }
      // 在用户点击的同步调用栈内创建窗口，避免异步请求后被浏览器判定为弹窗。
      const popup = window.open('', '_blank');
      if (!popup) {
        showNotice('浏览器阻止了新窗口，请允许弹出窗口后重试。', 'error');
        return;
      }
      popup.opener = null;
      setBusy('login', true);
      try {
        const data = await api('/admin/api/accounts/login/start', {
          method: 'POST',
          body: JSON.stringify({ alias: loginAlias.value, max_concurrency: maxConcurrency })
        });
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
      adminKey, accounts, settings, importJson, loginAlias, loginMaxConcurrency, connected, notice, showKey, modelTest,
      enabledCount, disabledCount, strategyLabel, isBusy, connect, loadAccounts, toggleAccount,
      refreshAccount, testAccount, removeAccount, setPriority, setMaxConcurrency, saveSettings, importAccounts,
      startLogin, formatDate, relativeExpiry, tokenState, initials,
      openModelTest, closeModelTest, runModelTest, configNameOf
    };
  }
}).mount('#app');
