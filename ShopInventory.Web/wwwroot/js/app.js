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
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        document.body.classList.remove('light-theme', 'dark-theme');
        document.body.classList.add(theme + '-theme');
    },

    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'light';
    }
};
