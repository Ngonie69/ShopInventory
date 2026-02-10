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

// Theme management
window.themeManager = {
    isTransitioning: false,

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
        metaThemeColor.content = theme === 'dark' ? '#0f172a' : '#f8fafc';

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

// File Download Handler - supports both signatures (from Kefalos-Workshop)
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
