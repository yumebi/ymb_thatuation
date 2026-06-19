// 各HTMLページ共通の多言語対応スクリプト。
// data-i18n / data-i18n-title / data-i18n-placeholder / data-i18n-html 属性を
// 翻訳辞書で置き換え、window.t(key, vars) でJS生成文字列の翻訳も行う。
(function () {
  const invoke =
    window.__TAURI__?.core?.invoke ??
    (window.__TAURI_INTERNALS__
      ? (cmd, args) => window.__TAURI_INTERNALS__.invoke(cmd, args)
      : null);

  let dict = {};

  function applyVars(str, vars) {
    if (!vars) return str;
    return str.replace(/\{(\w+)\}/g, (m, key) =>
      key in vars ? String(vars[key]) : m
    );
  }

  window.t = function (key, vars) {
    const str = dict[key] ?? key;
    return applyVars(str, vars);
  };

  function applyDom() {
    document.querySelectorAll("[data-i18n]").forEach((el) => {
      el.textContent = window.t(el.getAttribute("data-i18n"));
    });
    document.querySelectorAll("[data-i18n-title]").forEach((el) => {
      el.title = window.t(el.getAttribute("data-i18n-title"));
    });
    document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => {
      el.placeholder = window.t(el.getAttribute("data-i18n-placeholder"));
    });
    document.querySelectorAll("[data-i18n-html]").forEach((el) => {
      el.innerHTML = window.t(el.getAttribute("data-i18n-html"));
    });
    document.dispatchEvent(new CustomEvent("i18n-ready"));
  }

  async function load() {
    if (!invoke) return;
    try {
      const cfg = await invoke("get_config");
      const language = cfg?.settings?.language || "ja";
      dict = await invoke("get_translations", { language });
    } catch (e) {
      console.error("i18n init failed", e);
    }
    applyDom();
  }

  // 言語設定が変更された後に呼び出し、辞書とDOMを再適用する
  window.reloadI18n = load;

  window.i18nReady = load();
})();
