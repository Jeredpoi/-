const STORAGE_KEY = "submissionEntries";
const PENDING_STATUS_KEY = "pendingStatus";
const FORM_URL = "https://forms.gle/zCsbwhSNvqkXvCPS7";

const entriesList = document.getElementById("entries");
const lastSubmission = document.getElementById("lastSubmission");
const pendingStatus = document.getElementById("pendingStatus");
const deadlineStatus = document.getElementById("deadlineStatus");
const addEntryBtn = document.getElementById("addEntry");
const clearAllBtn = document.getElementById("clearAll");
const openFormBtn = document.getElementById("openForm");

function nowIso() {
  return new Date().toISOString();
}

function formatTimestamp(isoString) {
  const date = new Date(isoString);
  if (Number.isNaN(date.getTime())) {
    return "Неизвестная дата";
  }
  return new Intl.DateTimeFormat(navigator.language, {
    dateStyle: "medium",
    timeStyle: "medium"
  }).format(date);
}

function formatRelativeDay(isoString) {
  const date = new Date(isoString);
  if (Number.isNaN(date.getTime())) {
    return "";
  }
  const now = new Date();
  const startOfToday = new Date(
    now.getFullYear(),
    now.getMonth(),
    now.getDate()
  );
  const startOfDate = new Date(
    date.getFullYear(),
    date.getMonth(),
    date.getDate()
  );
  const diffDays = Math.round(
    (startOfToday.getTime() - startOfDate.getTime()) / (24 * 60 * 60 * 1000)
  );
  if (diffDays === 0) {
    return "сегодня";
  }
  if (diffDays === 1) {
    return "вчера";
  }
  if (diffDays > 1) {
    return `${diffDays} дн. назад`;
  }
  return "в будущем";
}

function getNextDeadline(dayOfWeek, hour) {
  const now = new Date();
  const next = new Date(now);
  const deltaDays = (dayOfWeek - next.getDay() + 7) % 7;
  next.setDate(next.getDate() + deltaDays);
  next.setHours(hour, 0, 0, 0);
  if (next <= now) {
    next.setDate(next.getDate() + 7);
  }
  return next;
}

function formatCountdown(targetDate) {
  const now = new Date();
  let diff = targetDate.getTime() - now.getTime();
  if (diff < 0) {
    diff = 0;
  }
  const hours = Math.floor(diff / (60 * 60 * 1000));
  const minutes = Math.floor((diff % (60 * 60 * 1000)) / (60 * 1000));
  return `${hours}ч ${minutes}м`;
}

function updateDeadlineStatus(entries) {
  const now = new Date();
  const lastEntry = entries[0] || null;
  const lastTs = lastEntry ? new Date(lastEntry.timestamp) : null;

  const nextWednesday = getNextDeadline(3, 18);
  const prevWednesday = new Date(nextWednesday);
  prevWednesday.setDate(prevWednesday.getDate() - 7);

  const submittedAfterPrev =
    lastTs && !Number.isNaN(lastTs.getTime()) && lastTs >= prevWednesday;

  if (submittedAfterPrev) {
    deadlineStatus.textContent = "";
    deadlineStatus.classList.remove("alert");
    return;
  }

  if (!submittedAfterPrev && now >= nextWednesday) {
    const nextSunday = getNextDeadline(0, 18);
    deadlineStatus.textContent = `СРОЧНО: до воскресенья осталось ${formatCountdown(
      nextSunday
    )}`;
    deadlineStatus.classList.add("alert");
    return;
  }

  deadlineStatus.textContent = `До дедлайна среды (18:00) осталось ${formatCountdown(
    nextWednesday
  )}`;
  deadlineStatus.classList.remove("alert");
}

function extractModeratorCount(entry) {
  if (!entry.answers) {
    return null;
  }
  for (const answer of entry.answers) {
    const question = (answer.question || "").toLowerCase();
    if (question.includes("модератор")) {
      const joined = answer.answers.join(" ");
      const match = joined.match(/\d+/);
      return match ? match[0] : joined;
    }
  }
  return null;
}

function formatUrl(url) {
  if (!url || url === "Добавлено вручную") {
    return url || "Google Form";
  }
  try {
    const parsed = new URL(url);
    return `${parsed.hostname}${parsed.pathname}`;
  } catch (error) {
    return url;
  }
}

function renderEntries(entries) {
  entriesList.innerHTML = "";

  if (!entries.length) {
    entriesList.innerHTML = '<li class="empty">Пока нет записей.</li>';
    lastSubmission.textContent = "Нет записей";
    updateDeadlineStatus([]);
    return;
  }

  const lastTitle = entries[0].title ? ` • ${entries[0].title}` : "";
  lastSubmission.textContent = `${formatTimestamp(entries[0].timestamp)}${lastTitle}`;
  updateDeadlineStatus(entries);

  for (const entry of entries) {
    const item = document.createElement("li");
    item.className = "entry";

    const topRow = document.createElement("div");
    topRow.className = "entry-top";

    const time = document.createElement("div");
    time.className = "entry-time";
    time.textContent = formatTimestamp(entry.timestamp);

    const actions = document.createElement("div");
    actions.className = "entry-actions";

    let detailsContainer = null;

    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn btn-danger";
    deleteBtn.textContent = "Удалить";
    deleteBtn.addEventListener("click", () => removeEntry(entry.id));

    const downloadBtn = document.createElement("button");
    downloadBtn.className = "btn btn-text";
    downloadBtn.textContent = "Скачать";
    downloadBtn.addEventListener("click", () => downloadEntry(entry));

    actions.appendChild(downloadBtn);
    actions.appendChild(deleteBtn);
    topRow.appendChild(time);
    topRow.appendChild(actions);

    const title = document.createElement("div");
    title.className = "entry-title";
    title.textContent = entry.title || "Google Form";

    const meta = document.createElement("div");
    meta.className = "entry-meta";
    meta.textContent = formatUrl(entry.url);

    item.appendChild(topRow);

    const relative = document.createElement("div");
    relative.className = "entry-relative";
    relative.textContent = formatRelativeDay(entry.timestamp);

    const moderatorCount = extractModeratorCount(entry);
    if (moderatorCount) {
      const chip = document.createElement("span");
      chip.className = "entry-chip";
      chip.textContent = `Модераторов: ${moderatorCount}`;
      relative.appendChild(document.createTextNode(" • "));
      relative.appendChild(chip);
    }

    item.appendChild(relative);
    item.appendChild(title);
    item.appendChild(meta);

    if (entry.answers && entry.answers.length) {
      detailsContainer = document.createElement("details");
      detailsContainer.className = "entry-details";

      const summary = document.createElement("summary");
      summary.textContent = "Показать ответы";
      detailsContainer.appendChild(summary);

      entry.answers.forEach((answer) => {
        const row = document.createElement("div");
        row.className = "entry-answer";

        const question = document.createElement("div");
        question.className = "entry-question";
        question.textContent = answer.question;

        const valueWrap = document.createElement("div");
        valueWrap.className = "entry-values";
        answer.answers.forEach((value) => {
          const valueItem = document.createElement("div");
          valueItem.className = "entry-value";
          valueItem.textContent = value;
          valueWrap.appendChild(valueItem);
        });

        row.appendChild(question);
        row.appendChild(valueWrap);
        detailsContainer.appendChild(row);
      });

      detailsContainer.addEventListener("toggle", () => {
        summary.textContent = detailsContainer.open
          ? "Скрыть ответы"
          : "Показать ответы";
      });

      item.appendChild(detailsContainer);
    }

    entriesList.appendChild(item);
  }
}

function renderPendingStatus(status) {
  const state = status ? status.state : "idle";
  pendingStatus.classList.remove(
    "status-idle",
    "status-active",
    "status-waiting",
    "status-recorded",
    "status-timeout"
  );
  pendingStatus.classList.add(`status-${state}`);

  if (!status || state === "idle") {
    pendingStatus.textContent = "Нет активности";
    return;
  }

  const timeLabel = formatTimestamp(status.timestamp);
  if (state === "waiting") {
    pendingStatus.textContent = `Получаем подтверждение... (${timeLabel})`;
    return;
  }
  if (state === "active") {
    pendingStatus.textContent = `Активно (${timeLabel})`;
    return;
  }
  if (state === "recorded") {
    const title = status.title ? ` • ${status.title}` : "";
    pendingStatus.textContent = `Ответ записан (${timeLabel})${title}`;
    return;
  }
  if (state === "timeout") {
    pendingStatus.textContent = `Нет подтверждения (${timeLabel})`;
    return;
  }
  pendingStatus.textContent = "Нет активности";
}

function formatEntryExport(entry) {
  const lines = [];
  lines.push(`Название: ${entry.title || "Google Form"}`);
  lines.push(`Время: ${formatTimestamp(entry.timestamp)}`);
  lines.push(`URL: ${entry.url || "—"}`);
  lines.push("");
  if (entry.answers && entry.answers.length) {
    lines.push("Ответы:");
    entry.answers.forEach((answer) => {
      lines.push(`- ${answer.question}`);
      answer.answers.forEach((value) => {
        lines.push(`  • ${value}`);
      });
    });
  } else {
    lines.push("Ответы: —");
  }
  return lines.join("\n");
}

function downloadEntry(entry) {
  const content = formatEntryExport(entry);
  const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  const safeDate = new Date(entry.timestamp).toISOString().slice(0, 19);
  a.href = url;
  a.download = `report-${safeDate}.txt`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
function getEntries() {
  return new Promise((resolve) => {
    chrome.storage.local.get([STORAGE_KEY], (result) => {
      resolve(result[STORAGE_KEY] || []);
    });
  });
}

function getPendingStatus() {
  return new Promise((resolve) => {
    chrome.storage.local.get([PENDING_STATUS_KEY], (result) => {
      resolve(result[PENDING_STATUS_KEY] || null);
    });
  });
}

function setEntries(entries) {
  return new Promise((resolve) => {
    chrome.storage.local.set({ [STORAGE_KEY]: entries }, () => resolve());
  });
}

async function addEntry() {
  const entries = await getEntries();
  const now = Date.now();
  const entry = {
    id: `manual-${now}`,
    formId: "manual",
    timestamp: nowIso(),
    url: "Добавлено вручную"
  };
  await setEntries([entry, ...entries].slice(0, 200));
  renderEntries(await getEntries());
}

async function removeEntry(entryId) {
  const entries = await getEntries();
  const next = entries.filter((entry) => entry.id !== entryId);
  await setEntries(next);
  renderEntries(next);
}

async function clearAll() {
  await setEntries([]);
  renderEntries([]);
}

addEntryBtn.addEventListener("click", () => {
  addEntry();
});

clearAllBtn.addEventListener("click", () => {
  clearAll();
});

openFormBtn.addEventListener("click", () => {
  chrome.tabs.create({ url: FORM_URL });
});

async function loadPopupData() {
  const [entries, status] = await Promise.all([
    getEntries(),
    getPendingStatus()
  ]);
  if (status && status.state === "waiting") {
    const ts = new Date(status.timestamp).getTime();
    const isStale =
      !Number.isNaN(ts) && Date.now() - ts > 10 * 60 * 1000;
    if (isStale) {
      const nextStatus = {
        state: "timeout",
        timestamp: new Date().toISOString(),
        title: status.title,
        url: status.url
      };
      chrome.storage.local.set({ [PENDING_STATUS_KEY]: nextStatus });
      renderPendingStatus(nextStatus);
    } else {
      renderPendingStatus(status);
    }
  } else if (status && status.state === "recorded") {
    const ts = new Date(status.timestamp).getTime();
    const isOld = !Number.isNaN(ts) && Date.now() - ts > 30 * 1000;
    if (isOld) {
      const nextStatus = {
        state: "idle",
        timestamp: new Date().toISOString()
      };
      chrome.storage.local.set({ [PENDING_STATUS_KEY]: nextStatus });
      renderPendingStatus(nextStatus);
    } else {
      renderPendingStatus(status);
    }
  } else {
    renderPendingStatus(status);
  }
  renderEntries(entries);
}

loadPopupData();

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName !== "local") {
    return;
  }
  if (changes[STORAGE_KEY]) {
    renderEntries(changes[STORAGE_KEY].newValue || []);
  }
  if (changes[PENDING_STATUS_KEY]) {
    renderPendingStatus(changes[PENDING_STATUS_KEY].newValue || null);
  }
});
