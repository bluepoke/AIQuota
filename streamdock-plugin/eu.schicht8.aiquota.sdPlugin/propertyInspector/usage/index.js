/// <reference path="../utils/common.js" />
/// <reference path="../utils/action.js" />

// Settings panel for the AIQuota action: picks which display style this key shows. The
// choice is saved as the key's "style" setting (via the $settings proxy from
// utils/action.js), which plugin/index.js passes on to AIQuota's GET /status.

const $local = false, $back = false, $dom = {
    main: $('.sdpi-wrapper'),
    style: $('#style'),
};

const $propEvent = {
    didReceiveSettings({ settings }) {
        $dom.style.value = settings.style || 'combined';
    },
    sendToPropertyInspector() { },
};

$dom.style.on('change', () => {
    $settings.style = $dom.style.value;
});
