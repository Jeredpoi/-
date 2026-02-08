const STORAGE_KEY = "submissionEntries";
const PENDING_KEY = "pendingSubmission";
const PENDING_STATUS_KEY = "pendingStatus";
const CONFIRMATION_TIMEOUT_MS = 15000;
const CONFIRMATION_CHECK_MS = 500;
const MIN_ENTRY_GAP_MS = 15000;
const FORM_CONFIG = [
  {
    shortUrl: "https://forms.gle/zCsbwhSNvqkXvCPS7",
    formId: "1FAIpQLSfJ2Tq20loiKS2hxmAV_JJjr4kLjVF-z2VnIqCkWtdBKaeCmA"
  },
  {
    shortUrl: "https://forms.gle/ipVLVoTxso49MkUZ9",
    formId: "1FAIpQLSf21tWWx7g_AJQluVjXgGUYv8hX06_Y-3D1LkkgqoygmjideQ"
  }
];

function getFormIdFromUrl() {
  const match = window.location.pathname.match(
    /\/forms\/(?:u\/\d+\/)?d\/e\/([^/]+)\//
  );
  return match ? match[1] : null;
}

function nowIso() {
  return new Date().toISOString();
}

function getFormTitle() {
  const heading =
    document.querySelector('div[role="heading"]') ||
    document.querySelector(".freebirdFormviewerViewHeaderTitle") ||
    document.querySelector("h1");
  if (heading && heading.textContent) {
    return heading.textContent.trim();
  }
  if (document.title) {
    return document.title.trim();
  }
  return "Google Form";
}

function isAllowedForm() {
  const formId = getFormIdFromUrl();
  if (formId) {
    return FORM_CONFIG.some((form) => form.formId === formId);
  }

  return FORM_CONFIG.some((form) =>
    window.location.href.startsWith(form.shortUrl)
  );
}

function isConfirmationPage() {
  const url = window.location.href;
  if (url.includes("formResponse")) {
    return true;
  }

  const bodyText = document.body ? document.body.innerText : "";
  if (!bodyText) {
    return false;
  }

  return (
    bodyText.includes("Your response has been recorded") ||
    bodyText.includes("Ваш ответ записан") ||
    bodyText.includes("Ответ записан") ||
    bodyText.includes("Спасибо за ответ")
  );
}

let confirmationTimer = null;
let isActive = true;

window.addEventListener("pagehide", () => {
  isActive = false;
  if (confirmationTimer) {
    clearInterval(confirmationTimer);
    confirmationTimer = null;
  }
});

async function setStatus(state, extra) {
  await setStorageValue({
    [PENDING_STATUS_KEY]: {
      state,
      timestamp: nowIso(),
      url: window.location.href,
      title: getFormTitle(),
      ...extra
    }
  });
  try {
    chrome.runtime.sendMessage({ type: "status-update", state });
  } catch (error) {
    // ignore
  }
}

async function setActiveStatusIfIdle() {
  const current = await getStorageValue(PENDING_STATUS_KEY, null);
  if (current && current.state === "waiting") {
    return;
  }
  await setStatus("active");
}

function startConfirmationWatch() {
  if (confirmationTimer) {
    return;
  }
  const startedAt = Date.now();
  confirmationTimer = setInterval(async () => {
    if (isConfirmationPage()) {
      clearInterval(confirmationTimer);
      confirmationTimer = null;
      await recordSubmission("confirmation-watch");
      return;
    }

    if (Date.now() - startedAt > CONFIRMATION_TIMEOUT_MS) {
      clearInterval(confirmationTimer);
      confirmationTimer = null;
      await setStatus("timeout");
    }
  }, CONFIRMATION_CHECK_MS);
}

async function getStorageValue(key, fallback) {
  return new Promise((resolve) => {
    if (!isActive || !chrome?.storage?.local) {
      resolve(fallback);
      return;
    }
    try {
      chrome.storage.local.get([key], (result) => {
        resolve(result[key] ?? fallback);
      });
    } catch (error) {
      resolve(fallback);
    }
  });
}

async function setStorageValue(values) {
  return new Promise((resolve) => {
    if (!isActive || !chrome?.storage?.local) {
      resolve();
      return;
    }
    try {
      chrome.storage.local.set(values, () => resolve());
    } catch (error) {
      resolve();
    }
  });
}

async function recordSubmission(source) {
  if (!isAllowedForm()) {
    return;
  }
  const formId = getFormIdFromUrl() || "unknown";
  const pending = await getStorageValue(PENDING_KEY, null);
  const pendingAnswers =
    pending && pending.formId === formId ? pending.answers : null;
  const pendingTitle =
    pending && pending.formId === formId ? pending.title : null;

  const entries = await getStorageValue(STORAGE_KEY, []);
  const last = entries[0];
  const now = Date.now();

  if (last && last.formId === formId) {
    const lastTime = new Date(last.timestamp).getTime();
    if (!Number.isNaN(lastTime) && now - lastTime < MIN_ENTRY_GAP_MS) {
      await setStatus("recorded", {
        title: last.title || getFormTitle(),
        url: last.url || window.location.href
      });
      return;
    }
  }

  const entry = {
    id: `${formId}-${now}`,
    formId,
    timestamp: nowIso(),
    url: window.location.href,
    title: pendingTitle || getFormTitle(),
    answers: pendingAnswers || [],
    source
  };

  const nextEntries = [entry, ...entries].slice(0, 200);
  await setStorageValue({
    [STORAGE_KEY]: nextEntries,
    [PENDING_KEY]: null
  });
  await setStatus("recorded", { title: entry.title, url: entry.url });
}

async function markPendingSubmission() {
  if (!isAllowedForm()) {
    return;
  }
  const formId = getFormIdFromUrl() || "unknown";
  const answers = collectAnswers();

  await setStorageValue({
    [PENDING_KEY]: {
      formId,
      timestamp: nowIso(),
      url: window.location.href,
      title: getFormTitle(),
      answers
    }
  });
  await setStatus("waiting");
}

function installSubmitListener() {
  const form = document.querySelector("form");
  if (!form) {
    return;
  }

  form.addEventListener(
    "submit",
    () => {
      markPendingSubmission();
      startConfirmationWatch();
    },
    { capture: true }
  );
}

function looksLikeSubmitButton(element) {
  const button = element.closest(
    'button, div[role="button"], span[role="button"]'
  );
  if (!button) {
    return false;
  }
  const text = (button.innerText || "").toLowerCase();
  return (
    text.includes("submit") ||
    text.includes("отправ") ||
    text.includes("подать")
  );
}

function getQuestionTitle(container) {
  const titleEl =
    container.querySelector('[role="heading"]') ||
    container.querySelector(".freebirdFormviewerViewItemsItemItemTitle") ||
    container.querySelector(".freebirdFormviewerViewItemsItemItemTitle") ||
    container.querySelector("label");
  return titleEl ? titleEl.textContent.trim() : "Вопрос";
}

function collectAnswers() {
  const results = [];
  const items = document.querySelectorAll('div[role="listitem"]');

  items.forEach((item) => {
    const question = getQuestionTitle(item);
    const answers = [];

    item
      .querySelectorAll('[role="radio"][aria-checked="true"]')
      .forEach((el) => {
        const label = el.getAttribute("aria-label") || el.textContent.trim();
        if (label) {
          answers.push(label);
        }
      });

    item
      .querySelectorAll('[role="checkbox"][aria-checked="true"]')
      .forEach((el) => {
        const label = el.getAttribute("aria-label") || el.textContent.trim();
        if (label) {
          answers.push(label);
        }
      });

    item.querySelectorAll("input, textarea, select").forEach((el) => {
      if (el.type === "radio" || el.type === "checkbox") {
        return;
      }
      if (el.tagName === "SELECT") {
        if (el.value) {
          answers.push(el.value);
        }
        return;
      }
      if (el.value && el.value.trim()) {
        answers.push(el.value.trim());
      }
    });

    if (answers.length) {
      results.push({ question, answers });
    }
  });

  return results;
}

function installClickListener() {
  document.addEventListener(
    "click",
    (event) => {
      if (!isAllowedForm()) {
        return;
      }
      if (!looksLikeSubmitButton(event.target)) {
        return;
      }
      markPendingSubmission();
      startConfirmationWatch();
    },
    true
  );
}

async function checkConfirmation() {
  if (!isAllowedForm()) {
    return;
  }
  if (!isConfirmationPage()) {
    return;
  }

  const pending = await getStorageValue(PENDING_KEY, null);
  if (!pending) {
    await recordSubmission("confirmation");
    return;
  }

  const currentFormId = getFormIdFromUrl() || "unknown";
  if (currentFormId && pending.formId === currentFormId) {
    await recordSubmission("confirmation");
    return;
  }
  await recordSubmission("confirmation-mismatch");
}

installSubmitListener();
installClickListener();
checkConfirmation();
if (isAllowedForm()) {
  setActiveStatusIfIdle();
}

const observer = new MutationObserver(() => {
  checkConfirmation();
});

observer.observe(document.documentElement, {
  childList: true,
  subtree: true
});
