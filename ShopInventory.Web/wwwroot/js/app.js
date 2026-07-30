// Keyboard Shortcuts Handler for ShopInventory
window.keyboardShortcuts = {
    dotNetRef: null,
    keySequence: [],
    sequenceTimeout: null,

    initialize: function (dotNetReference) {
        this.dotNetRef = dotNetReference;
        this.keySequence = [];

        document.addEventListener('keydown', this.handleKeyDown.bind(this));
    },

    handleKeyDown: function (e) {
        // Don't trigger shortcuts when typing in inputs
        const tagName = e.target.tagName.toLowerCase();
        const isEditable = e.target.isContentEditable;
        const isInput = tagName === 'input' || tagName === 'textarea' || tagName === 'select' || isEditable;

        // Allow Escape to always work
        if (e.key === 'Escape') {
            // Don't prevent default for escape in inputs
            if (!isInput) {
                this.invokeShortcut('escape');
            }
            return;
        }

        // Skip if in input field (except for special shortcuts)
        if (isInput) {
            // Allow Ctrl+K even in inputs
            if (e.ctrlKey && e.key.toLowerCase() === 'k') {
                e.preventDefault();
                this.invokeShortcut('search');
            }
            return;
        }

        // Ctrl + K - Global Search
        if (e.ctrlKey && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            this.invokeShortcut('search');
            return;
        }

        // Ctrl + Shift + D - Toggle Dark Mode
        if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'd') {
            e.preventDefault();
            this.invokeShortcut('toggleTheme');
            return;
        }

        // ? - Show Help (Shift + /)
        if (e.key === '?') {
            e.preventDefault();
            this.invokeShortcut('help');
            return;
        }

        // F5 or Ctrl+R - Refresh
        if (e.key === 'F5' || (e.ctrlKey && e.key.toLowerCase() === 'r')) {
            // Allow default browser refresh
            return;
        }

        // Handle key sequences (G then H, G then I, etc.)
        this.handleKeySequence(e);
    },

    handleKeySequence: function (e) {
        // Clear previous sequence after timeout
        if (this.sequenceTimeout) {
            clearTimeout(this.sequenceTimeout);
        }

        this.keySequence.push(e.key.toLowerCase());

        // Keep only last 2 keys
        if (this.keySequence.length > 2) {
            this.keySequence.shift();
        }

        const sequence = this.keySequence.join('');

        // G then H - Go Home
        if (sequence === 'gh') {
            e.preventDefault();
            this.invokeShortcut('goHome');
            this.keySequence = [];
            return;
        }

        // G then I - Go to Invoices
        if (sequence === 'gi') {
            e.preventDefault();
            window.location.href = '/invoices';
            this.keySequence = [];
            return;
        }

        // G then P - Go to Products
        if (sequence === 'gp') {
            e.preventDefault();
            window.location.href = '/products';
            this.keySequence = [];
            return;
        }

        // G then R - Go to Reports
        if (sequence === 'gr') {
            e.preventDefault();
            window.location.href = '/reports';
            this.keySequence = [];
            return;
        }

        // N then I - New Invoice
        if (sequence === 'ni') {
            e.preventDefault();
            this.invokeShortcut('createInvoice');
            this.keySequence = [];
            return;
        }

        // Reset sequence after 1 second
        this.sequenceTimeout = setTimeout(() => {
            this.keySequence = [];
        }, 1000);
    },

    invokeShortcut: function (shortcut) {
        if (this.dotNetRef) {
            this.dotNetRef.invokeMethodAsync('HandleShortcut', shortcut);
        }
    },

    dispose: function () {
        document.removeEventListener('keydown', this.handleKeyDown);
        this.dotNetRef = null;
    }
};

window.dashboardSearch = {
    inputId: null,
    handler: null,

    init: function (inputId) {
        this.dispose();
        this.inputId = inputId;
        this.handler = function (event) {
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
                event.preventDefault();
                const input = document.getElementById(inputId);
                if (input) {
                    input.focus();
                    input.select();
                }
            }
        };
        document.addEventListener('keydown', this.handler);
    },

    dispose: function () {
        if (this.handler) {
            document.removeEventListener('keydown', this.handler);
        }
        this.handler = null;
        this.inputId = null;
    }
};

window.dashboardProfile = {
    detailsId: null,
    pointerHandler: null,
    keyHandler: null,

    init: function (detailsId) {
        this.dispose();
        this.detailsId = detailsId;
        this.pointerHandler = function (event) {
            const details = document.getElementById(detailsId);
            if (details && details.open && !details.contains(event.target)) {
                details.open = false;
            }
        };
        this.keyHandler = function (event) {
            if (event.key === 'Escape') {
                const details = document.getElementById(detailsId);
                if (details && details.open) {
                    details.open = false;
                    details.querySelector('summary')?.focus();
                }
            }
        };
        document.addEventListener('pointerdown', this.pointerHandler);
        document.addEventListener('keydown', this.keyHandler);
    },

    dispose: function () {
        if (this.pointerHandler) {
            document.removeEventListener('pointerdown', this.pointerHandler);
        }
        if (this.keyHandler) {
            document.removeEventListener('keydown', this.keyHandler);
        }
        this.detailsId = null;
        this.pointerHandler = null;
        this.keyHandler = null;
    }
};

// Theme management
window.themeManager = {
    isTransitioning: false,

    getPreferredTheme: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
            ? 'dark'
            : 'light';
    },

    setTheme: function (theme, animate = true) {
        const html = document.documentElement;
        const body = document.body;
        const page = document.querySelector('.page');

        // Enable smooth transitions (if animating and not initial load)
        if (animate && html.classList.contains('theme-ready')) {
            html.classList.add('theme-transitioning');
            this.isTransitioning = true;
        }

        // Set data attributes for Bootstrap/MudBlazor compatibility
        html.setAttribute('data-theme', theme);
        html.setAttribute('data-bs-theme', theme);

        // Apply theme class to html, body, and page element (for Blazor scoped CSS)
        html.classList.remove('light-theme', 'dark-theme');
        html.classList.add(theme + '-theme');
        body.classList.remove('light-theme', 'dark-theme');
        body.classList.add(theme + '-theme');

        // Also apply to .page element for Blazor component scoped CSS
        if (page) {
            page.classList.remove('light-theme', 'dark-theme');
            if (theme === 'dark') {
                page.classList.add('dark-theme');
            }
        }

        // Update color-scheme meta tag
        let metaColorScheme = document.querySelector('meta[name="color-scheme"]');
        if (!metaColorScheme) {
            metaColorScheme = document.createElement('meta');
            metaColorScheme.name = 'color-scheme';
            document.head.appendChild(metaColorScheme);
        }
        metaColorScheme.content = theme === 'dark' ? 'dark' : 'light';

        // Update theme-color meta tag for browser chrome
        let metaThemeColor = document.querySelector('meta[name="theme-color"]');
        if (!metaThemeColor) {
            metaThemeColor = document.createElement('meta');
            metaThemeColor.name = 'theme-color';
            document.head.appendChild(metaThemeColor);
        }
        metaThemeColor.content = theme === 'dark' ? '#071425' : '#F6F8FB';

        // Remove transition class after animation completes
        if (animate && this.isTransitioning) {
            setTimeout(() => {
                html.classList.remove('theme-transitioning');
                this.isTransitioning = false;
            }, 350);
        }

        // Mark theme as ready for future transitions
        if (!html.classList.contains('theme-ready')) {
            requestAnimationFrame(() => {
                html.classList.add('theme-ready');
            });
        }
    },

    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'light';
    },

    // Initialize theme without animation (for page load)
    initTheme: function (theme) {
        this.setTheme(theme, false);
    }
};

window.shopInventory = window.shopInventory || {};

window.shopInventory.formatUtcForBrowserLocalDisplay = function (utcIsoString) {
    if (!utcIsoString) {
        return null;
    }

    const date = new Date(utcIsoString);
    if (Number.isNaN(date.getTime())) {
        return null;
    }

    const formatter = new Intl.DateTimeFormat(undefined, {
        day: '2-digit',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        hour12: false,
        timeZoneName: 'short'
    });

    const parts = formatter.formatToParts(date);
    const day = parts.find(part => part.type === 'day')?.value;
    const month = parts.find(part => part.type === 'month')?.value;
    const year = parts.find(part => part.type === 'year')?.value;
    const hour = parts.find(part => part.type === 'hour')?.value;
    const minute = parts.find(part => part.type === 'minute')?.value;
    const timeZoneName = parts.find(part => part.type === 'timeZoneName')?.value;

    if (!day || !month || !year || !hour || !minute) {
        return formatter.format(date);
    }

    return `${day} ${month} ${year} ${hour}:${minute}${timeZoneName ? ` ${timeZoneName}` : ''}`;
};

// File Download Handler - supports both signatures
window.downloadFile = function (fileName, contentTypeOrBase64, base64Content) {
    let base64Data, contentType;

    // Check if called with 2 parameters (fileName, base64) or 3 parameters (fileName, contentType, base64)
    if (arguments.length === 2) {
        base64Data = contentTypeOrBase64;
        // Auto-detect content type based on file extension
        if (fileName.endsWith('.xlsx')) {
            contentType = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
        } else if (fileName.endsWith('.pdf')) {
            contentType = 'application/pdf';
        } else if (fileName.endsWith('.csv')) {
            contentType = 'text/csv';
        } else {
            contentType = 'application/octet-stream';
        }
    } else {
        contentType = contentTypeOrBase64;
        base64Data = base64Content;
    }

    // Decode base64 and create blob
    const binaryString = atob(base64Data);
    const byteArray = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        byteArray[i] = binaryString.charCodeAt(i);
    }

    const blob = new Blob([byteArray], { type: contentType });
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.setAttribute('download', fileName);
    document.body.appendChild(link);
    link.click();
    link.parentNode.removeChild(link);
    window.URL.revokeObjectURL(url);
};

function getStoredBearerToken(tokenKey) {
    const key = tokenKey || 'authToken';
    const token = normalizeStoredToken(localStorage.getItem(key));
    if (!token) {
        throw new Error('Authentication token is missing. Please sign in again.');
    }

    return token;
}

function normalizeStoredToken(value) {
    if (!value) {
        return '';
    }

    const trimmed = String(value).trim();
    if (!trimmed) {
        return '';
    }

    try {
        const parsed = JSON.parse(trimmed);
        if (typeof parsed === 'string') {
            return parsed.trim();
        }
    } catch {
        // Tokens written without Blazored.LocalStorage JSON serialization are already usable.
    }

    return trimmed;
}

function getFileNameFromDisposition(disposition, fallbackFileName) {
    if (!disposition) {
        return fallbackFileName || 'download';
    }

    const utf8Match = disposition.match(/filename\*=UTF-8''([^;]+)/i);
    if (utf8Match?.[1]) {
        return decodeURIComponent(utf8Match[1].replace(/"/g, '').trim());
    }

    const fileNameMatch = disposition.match(/filename="?([^";]+)"?/i);
    return fileNameMatch?.[1]?.trim() || fallbackFileName || 'download';
}

async function fetchAuthenticatedBlob(url, tokenKey, fallbackFileName) {
    const token = getStoredBearerToken(tokenKey);
    const response = await fetch(url, {
        method: 'GET',
        headers: {
            'Authorization': `Bearer ${token}`
        },
        credentials: 'same-origin'
    });

    if (!response.ok) {
        throw new Error(`Download failed with status ${response.status}`);
    }

    return {
        blob: await response.blob(),
        fileName: getFileNameFromDisposition(response.headers.get('Content-Disposition'), fallbackFileName)
    };
}

window.downloadAuthenticatedFile = async function (url, fallbackFileName, tokenKey) {
    const result = await fetchAuthenticatedBlob(url, tokenKey || 'authToken', fallbackFileName);
    const objectUrl = URL.createObjectURL(result.blob);
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = result.fileName || fallbackFileName || 'download';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    setTimeout(function () { URL.revokeObjectURL(objectUrl); }, 5000);
};

window.createAuthenticatedObjectUrl = async function (url, tokenKey) {
    const result = await fetchAuthenticatedBlob(url, tokenKey || 'authToken');
    return URL.createObjectURL(result.blob);
};

window.createObjectUrlFromBase64 = function (contentType, base64Content) {
    const binaryString = atob(base64Content);
    const byteArray = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        byteArray[i] = binaryString.charCodeAt(i);
    }

    const blob = new Blob([byteArray], { type: contentType || 'application/octet-stream' });
    return URL.createObjectURL(blob);
};

window.revokeObjectUrl = function (url) {
    if (url && url.startsWith('blob:')) {
        URL.revokeObjectURL(url);
    }
};

// Print HTML content using a hidden iframe (avoids popup blocker issues in Blazor Server)
window.printReportHtml = function (htmlContent) {
    var iframe = document.getElementById('_reportPrintFrame');
    if (!iframe) {
        iframe = document.createElement('iframe');
        iframe.id = '_reportPrintFrame';
        iframe.style.position = 'fixed';
        iframe.style.right = '0';
        iframe.style.bottom = '0';
        iframe.style.width = '0';
        iframe.style.height = '0';
        iframe.style.border = 'none';
        document.body.appendChild(iframe);
    }
    var doc = iframe.contentDocument || iframe.contentWindow.document;
    doc.open();
    doc.write(htmlContent);
    doc.close();
    // Wait for content to render, then print
    setTimeout(function () {
        iframe.contentWindow.focus();
        iframe.contentWindow.print();
    }, 300);
};

// Print a PDF from base64-encoded bytes using an embedded iframe.
// This hands off to the browser's own print dialog, which is the only thing that can choose a
// printer or a copy count from a web page. We therefore call print() exactly once: an earlier
// version looped it `copies` times, which stacked that many modal dialogs on the cashier instead
// of producing that many copies. Copies are set by the operator in the dialog.
window.printPdfFromBase64 = function (base64Data) {
    const binaryString = atob(base64Data);
    const byteArray = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        byteArray[i] = binaryString.charCodeAt(i);
    }

    const blob = new Blob([byteArray], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);

    // Use a hidden iframe for printing – avoids popup blockers
    let iframe = document.getElementById('_pdfPrintFrame');
    if (!iframe) {
        iframe = document.createElement('iframe');
        iframe.id = '_pdfPrintFrame';
        iframe.style.position = 'fixed';
        iframe.style.right = '0';
        iframe.style.bottom = '0';
        iframe.style.width = '0';
        iframe.style.height = '0';
        iframe.style.border = 'none';
        document.body.appendChild(iframe);
    }

    iframe.src = url;

    iframe.onload = function () {
        try {
            // Small delay to ensure PDF is fully rendered
            setTimeout(function () {
                iframe.contentWindow.focus();
                iframe.contentWindow.print();
                // Clean up blob URL after a delay
                setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
            }, 500);
        } catch (e) {
            // Fallback: open in new tab if iframe print fails (e.g. cross-origin)
            window.open(url, '_blank');
        }
    };
};

// Download a PDF file from base64-encoded bytes
window.downloadPdfFromBase64 = function (base64Data, fileName) {
    const binaryString = atob(base64Data);
    const byteArray = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        byteArray[i] = binaryString.charCodeAt(i);
    }

    const blob = new Blob([byteArray], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName || 'invoice.pdf';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
};
