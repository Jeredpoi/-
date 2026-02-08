const STORAGE_KEY = "submissionEntries";
const FORM_ID_MAIN =
  "1FAIpQLSfJ2Tq20loiKS2hxmAV_JJjr4kLjVF-z2VnIqCkWtdBKaeCmA";

const ALARM_SATURDAY = "reminderSaturday";
const ALARM_SUNDAY = "reminderSunday";

const ICON_URL = "icon.svg";

function scheduleWeeklyAlarm(alarmName, targetDay, targetHour) {
  const now = new Date();
  const next = new Date(now);
  const day = next.getDay();
  let deltaDays = (targetDay - day + 7) % 7;
  next.setDate(next.getDate() + deltaDays);
  next.setHours(targetHour, 0, 0, 0);

  if (next <= now) {
    next.setDate(next.getDate() + 7);
  }

  chrome.alarms.create(alarmName, { when: next.getTime() });
}

function ensureAlarms() {
  scheduleWeeklyAlarm(ALARM_SATURDAY, 6, 18);
  scheduleWeeklyAlarm(ALARM_SUNDAY, 0, 13);
}

function getCurrentCycleStart(now = new Date()) {
  const start = new Date(now);
  const day = start.getDay();
  const daysSinceSaturday = (day - 6 + 7) % 7;
  start.setDate(start.getDate() - daysSinceSaturday);
  start.setHours(18, 0, 0, 0);
  if (start > now) {
    start.setDate(start.getDate() - 7);
  }
  return start;
}

async function hasSubmittedThisCycle() {
  return new Promise((resolve) => {
    chrome.storage.local.get([STORAGE_KEY], (result) => {
      const entries = result[STORAGE_KEY] || [];
      const cycleStart = getCurrentCycleStart();
      const submitted = entries.some((entry) => {
        if (entry.formId !== FORM_ID_MAIN) {
          return false;
        }
        const ts = new Date(entry.timestamp).getTime();
        return !Number.isNaN(ts) && ts >= cycleStart.getTime();
      });
      resolve(submitted);
    });
  });
}

async function showNotification(title, message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: ICON_URL,
    title,
    message
  });
}

async function handleReminder(type) {
  const submitted = await hasSubmittedThisCycle();
  if (submitted) {
    return;
  }

  if (type === ALARM_SATURDAY) {
    await showNotification(
      "Пора подать отчет",
      "Напоминание: отчет нужно отправить до 18:00."
    );
  }

  if (type === ALARM_SUNDAY) {
    await showNotification(
      "Срочно подайте отчет",
      "Отчет не подан. Дедлайн сегодня в 18:00."
    );
  }
}

function setBadgeForState(state) {
  if (state === "waiting") {
    chrome.action.setBadgeText({ text: "…" });
    chrome.action.setBadgeBackgroundColor({ color: "#2b57ff" });
    return;
  }
  if (state === "recorded") {
    chrome.action.setBadgeText({ text: "OK" });
    chrome.action.setBadgeBackgroundColor({ color: "#1e7f3e" });
    return;
  }
  if (state === "timeout") {
    chrome.action.setBadgeText({ text: "!" });
    chrome.action.setBadgeBackgroundColor({ color: "#b02a2a" });
    return;
  }
  if (state === "active") {
    chrome.action.setBadgeText({ text: "•" });
    chrome.action.setBadgeBackgroundColor({ color: "#1e7f3e" });
    return;
  }

  chrome.action.setBadgeText({ text: "" });
}

chrome.runtime.onInstalled.addListener(() => {
  ensureAlarms();
});

chrome.runtime.onStartup.addListener(() => {
  ensureAlarms();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  handleReminder(alarm.name);
  if (alarm.name === ALARM_SATURDAY) {
    scheduleWeeklyAlarm(ALARM_SATURDAY, 6, 18);
  }
  if (alarm.name === ALARM_SUNDAY) {
    scheduleWeeklyAlarm(ALARM_SUNDAY, 0, 13);
  }
});

chrome.runtime.onMessage.addListener((message) => {
  if (message && message.type === "status-update") {
    setBadgeForState(message.state);
  }
});
