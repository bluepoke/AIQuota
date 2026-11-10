/// <reference path="../utils/common.js" />
/// <reference path="../utils/action.js" />

// Settings panel for the AIQuota action: picks which display style this key shows, and
// whether and from which percentage it flashes. The choices are saved as the key's "style",
// "flashEnabled" and "flashThreshold" settings (via the $settings proxy from
// utils/action.js), which plugin/index.js receives via didReceiveSettings and applies
// right away.

const DEFAULT_FLASH_THRESHOLD = 90;

const $local = false, $back = false, $dom = {
    main: $('.sdpi-wrapper'),
    style: $('#style'),
    flashEnabled: $('#flashEnabled'),
    flashThreshold: $('#flashThreshold'),
};

// A whole number from 1 to 100, or null.
function parseThreshold(value) {
    const number = Number(value);
    return Number.isInteger(number) && number >= 1 && number <= 100 ? number : null;
}

const $propEvent = {
    didReceiveSettings({ settings }) {
        // Keys placed before flashing existed have no flash settings: on, at 90%.
        const enabled = settings.flashEnabled ?? true;
        $dom.style.value = settings.style || 'combined';
        $dom.flashEnabled.checked = enabled;
        $dom.flashThreshold.value = parseThreshold(settings.flashThreshold) ?? DEFAULT_FLASH_THRESHOLD;
        $dom.flashThreshold.disabled = !enabled;
    },
    sendToPropertyInspector() { },
};

$dom.style.on('change', () => {
    $settings.style = $dom.style.value;
});

$dom.flashEnabled.on('change', () => {
    $dom.flashThreshold.disabled = !$dom.flashEnabled.checked;
    $settings.flashEnabled = $dom.flashEnabled.checked;
});

// Saved shortly after typing stops (so typing "95" doesn't briefly save 9%), and right
// away on leaving the field or using its arrows. Invalid entries aren't saved; on leaving
// the field they're put back to the last saved value.
let thresholdTimer = null;
function saveThreshold() {
    clearTimeout(thresholdTimer);
    const threshold = parseThreshold($dom.flashThreshold.value);
    if (threshold !== null && threshold !== $settings.flashThreshold) $settings.flashThreshold = threshold;
}
$dom.flashThreshold.on('input', () => {
    clearTimeout(thresholdTimer);
    thresholdTimer = setTimeout(saveThreshold, 700);
});
$dom.flashThreshold.on('change', () => {
    saveThreshold();
    $dom.flashThreshold.value = parseThreshold($dom.flashThreshold.value)
        ?? parseThreshold($settings.flashThreshold)
        ?? DEFAULT_FLASH_THRESHOLD;
});
