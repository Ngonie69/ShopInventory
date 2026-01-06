using Blazored.LocalStorage;
using System.Globalization;

namespace ShopInventory.Web.Services;

public interface ILocalizationService
{
    event Action? OnLanguageChanged;
    string CurrentLanguage { get; }
    CultureInfo CurrentCulture { get; }
    Task<string> GetLanguageAsync();
    Task SetLanguageAsync(string languageCode);
    string Translate(string key);
    string Translate(string key, params object[] args);
    IReadOnlyList<LanguageInfo> SupportedLanguages { get; }
}

public class LanguageInfo
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string Flag { get; set; } = string.Empty;
}

public class LocalizationService : ILocalizationService
{
    private readonly ILocalStorageService _localStorage;
    private const string LanguageKey = "app-language";
    private string _currentLanguage = "en";

    public event Action? OnLanguageChanged;

    public string CurrentLanguage => _currentLanguage;
    public CultureInfo CurrentCulture => new CultureInfo(_currentLanguage);

    public IReadOnlyList<LanguageInfo> SupportedLanguages { get; } = new List<LanguageInfo>
    {
        new() { Code = "en", Name = "English", NativeName = "English", Flag = "🇺🇸" },
        new() { Code = "es", Name = "Spanish", NativeName = "Español", Flag = "🇪🇸" },
        new() { Code = "fr", Name = "French", NativeName = "Français", Flag = "🇫🇷" },
        new() { Code = "de", Name = "German", NativeName = "Deutsch", Flag = "🇩🇪" },
        new() { Code = "pt", Name = "Portuguese", NativeName = "Português", Flag = "🇧🇷" },
        new() { Code = "zh", Name = "Chinese", NativeName = "中文", Flag = "🇨🇳" },
        new() { Code = "ar", Name = "Arabic", NativeName = "العربية", Flag = "🇸🇦" },
        new() { Code = "sw", Name = "Swahili", NativeName = "Kiswahili", Flag = "🇰🇪" }
    };

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["en"] = new Dictionary<string, string>
        {
            // Navigation
            ["nav.dashboard"] = "Dashboard",
            ["nav.invoices"] = "Invoices",
            ["nav.createInvoice"] = "Create Invoice",
            ["nav.inventory"] = "Inventory Transfers",
            ["nav.payments"] = "Payments",
            ["nav.products"] = "Products",
            ["nav.prices"] = "Price List",
            ["nav.reports"] = "Reports",
            ["nav.syncStatus"] = "Sync Status",
            ["nav.users"] = "Users",
            ["nav.permissions"] = "Permissions & Security",
            ["nav.userActivity"] = "User Activity",
            ["nav.auditTrail"] = "Audit Trail",
            ["nav.settings"] = "Settings",

            // Common
            ["common.search"] = "Search",
            ["common.filter"] = "Filter",
            ["common.clear"] = "Clear",
            ["common.save"] = "Save",
            ["common.cancel"] = "Cancel",
            ["common.delete"] = "Delete",
            ["common.edit"] = "Edit",
            ["common.view"] = "View",
            ["common.create"] = "Create",
            ["common.loading"] = "Loading...",
            ["common.noData"] = "No data available",
            ["common.actions"] = "Actions",
            ["common.status"] = "Status",
            ["common.date"] = "Date",
            ["common.amount"] = "Amount",
            ["common.total"] = "Total",
            ["common.logout"] = "Logout",
            ["common.login"] = "Login",
            ["common.welcome"] = "Welcome back",

            // Invoice
            ["invoice.docNum"] = "Doc Number",
            ["invoice.cardCode"] = "Customer Code",
            ["invoice.cardName"] = "Customer Name",
            ["invoice.docDate"] = "Document Date",
            ["invoice.dueDate"] = "Due Date",
            ["invoice.docTotal"] = "Total",
            ["invoice.createNew"] = "Create New Invoice",

            // Product
            ["product.itemCode"] = "Item Code",
            ["product.itemName"] = "Item Name",
            ["product.quantity"] = "Quantity",
            ["product.price"] = "Price",
            ["product.stock"] = "In Stock",

            // Messages
            ["msg.success"] = "Operation completed successfully",
            ["msg.error"] = "An error occurred",
            ["msg.confirm"] = "Are you sure?",
            ["msg.saved"] = "Changes saved",
            ["msg.deleted"] = "Item deleted",

            // Theme
            ["theme.light"] = "Light Mode",
            ["theme.dark"] = "Dark Mode",
            ["theme.toggle"] = "Toggle Theme",

            // Search
            ["search.placeholder"] = "Search invoices, products, customers...",
            ["search.noResults"] = "No results found",
            ["search.hint"] = "Press Ctrl+K to search"
        },
        ["es"] = new Dictionary<string, string>
        {
            ["nav.dashboard"] = "Tablero",
            ["nav.invoices"] = "Facturas",
            ["nav.createInvoice"] = "Crear Factura",
            ["nav.inventory"] = "Transferencias de Inventario",
            ["nav.payments"] = "Pagos",
            ["nav.products"] = "Productos",
            ["nav.prices"] = "Lista de Precios",
            ["nav.reports"] = "Informes",
            ["nav.syncStatus"] = "Estado de Sincronización",
            ["nav.users"] = "Usuarios",
            ["nav.permissions"] = "Permisos y Seguridad",
            ["nav.userActivity"] = "Actividad de Usuario",
            ["nav.auditTrail"] = "Registro de Auditoría",
            ["nav.settings"] = "Configuración",

            ["common.search"] = "Buscar",
            ["common.filter"] = "Filtrar",
            ["common.clear"] = "Limpiar",
            ["common.save"] = "Guardar",
            ["common.cancel"] = "Cancelar",
            ["common.delete"] = "Eliminar",
            ["common.edit"] = "Editar",
            ["common.view"] = "Ver",
            ["common.create"] = "Crear",
            ["common.loading"] = "Cargando...",
            ["common.noData"] = "No hay datos disponibles",
            ["common.actions"] = "Acciones",
            ["common.status"] = "Estado",
            ["common.date"] = "Fecha",
            ["common.amount"] = "Monto",
            ["common.total"] = "Total",
            ["common.logout"] = "Cerrar Sesión",
            ["common.login"] = "Iniciar Sesión",
            ["common.welcome"] = "Bienvenido de nuevo",

            ["invoice.docNum"] = "Número de Documento",
            ["invoice.cardCode"] = "Código de Cliente",
            ["invoice.cardName"] = "Nombre de Cliente",
            ["invoice.docDate"] = "Fecha del Documento",
            ["invoice.dueDate"] = "Fecha de Vencimiento",
            ["invoice.docTotal"] = "Total",
            ["invoice.createNew"] = "Crear Nueva Factura",

            ["product.itemCode"] = "Código de Artículo",
            ["product.itemName"] = "Nombre de Artículo",
            ["product.quantity"] = "Cantidad",
            ["product.price"] = "Precio",
            ["product.stock"] = "En Stock",

            ["msg.success"] = "Operación completada exitosamente",
            ["msg.error"] = "Ocurrió un error",
            ["msg.confirm"] = "¿Está seguro?",
            ["msg.saved"] = "Cambios guardados",
            ["msg.deleted"] = "Elemento eliminado",

            ["theme.light"] = "Modo Claro",
            ["theme.dark"] = "Modo Oscuro",
            ["theme.toggle"] = "Cambiar Tema",

            ["search.placeholder"] = "Buscar facturas, productos, clientes...",
            ["search.noResults"] = "No se encontraron resultados",
            ["search.hint"] = "Presione Ctrl+K para buscar"
        },
        ["fr"] = new Dictionary<string, string>
        {
            ["nav.dashboard"] = "Tableau de Bord",
            ["nav.invoices"] = "Factures",
            ["nav.createInvoice"] = "Créer une Facture",
            ["nav.inventory"] = "Transferts d'Inventaire",
            ["nav.payments"] = "Paiements",
            ["nav.products"] = "Produits",
            ["nav.prices"] = "Liste de Prix",
            ["nav.reports"] = "Rapports",
            ["nav.syncStatus"] = "État de Synchronisation",
            ["nav.users"] = "Utilisateurs",
            ["nav.permissions"] = "Permissions et Sécurité",
            ["nav.userActivity"] = "Activité Utilisateur",
            ["nav.auditTrail"] = "Journal d'Audit",
            ["nav.settings"] = "Paramètres",

            ["common.search"] = "Rechercher",
            ["common.filter"] = "Filtrer",
            ["common.clear"] = "Effacer",
            ["common.save"] = "Enregistrer",
            ["common.cancel"] = "Annuler",
            ["common.delete"] = "Supprimer",
            ["common.edit"] = "Modifier",
            ["common.view"] = "Voir",
            ["common.create"] = "Créer",
            ["common.loading"] = "Chargement...",
            ["common.noData"] = "Aucune donnée disponible",
            ["common.actions"] = "Actions",
            ["common.status"] = "Statut",
            ["common.date"] = "Date",
            ["common.amount"] = "Montant",
            ["common.total"] = "Total",
            ["common.logout"] = "Déconnexion",
            ["common.login"] = "Connexion",
            ["common.welcome"] = "Bon retour",

            ["invoice.docNum"] = "Numéro de Document",
            ["invoice.cardCode"] = "Code Client",
            ["invoice.cardName"] = "Nom du Client",
            ["invoice.docDate"] = "Date du Document",
            ["invoice.dueDate"] = "Date d'Échéance",
            ["invoice.docTotal"] = "Total",
            ["invoice.createNew"] = "Créer une Nouvelle Facture",

            ["product.itemCode"] = "Code Article",
            ["product.itemName"] = "Nom de l'Article",
            ["product.quantity"] = "Quantité",
            ["product.price"] = "Prix",
            ["product.stock"] = "En Stock",

            ["msg.success"] = "Opération réussie",
            ["msg.error"] = "Une erreur s'est produite",
            ["msg.confirm"] = "Êtes-vous sûr?",
            ["msg.saved"] = "Modifications enregistrées",
            ["msg.deleted"] = "Élément supprimé",

            ["theme.light"] = "Mode Clair",
            ["theme.dark"] = "Mode Sombre",
            ["theme.toggle"] = "Changer le Thème",

            ["search.placeholder"] = "Rechercher factures, produits, clients...",
            ["search.noResults"] = "Aucun résultat trouvé",
            ["search.hint"] = "Appuyez sur Ctrl+K pour rechercher"
        },
        ["sw"] = new Dictionary<string, string>
        {
            ["nav.dashboard"] = "Dashibodi",
            ["nav.invoices"] = "Ankara",
            ["nav.createInvoice"] = "Tengeneza Ankara",
            ["nav.inventory"] = "Uhamisho wa Bidhaa",
            ["nav.payments"] = "Malipo",
            ["nav.products"] = "Bidhaa",
            ["nav.prices"] = "Orodha ya Bei",
            ["nav.reports"] = "Ripoti",
            ["nav.syncStatus"] = "Hali ya Usawazishaji",
            ["nav.users"] = "Watumiaji",
            ["nav.permissions"] = "Ruhusa na Usalama",
            ["nav.userActivity"] = "Shughuli za Mtumiaji",
            ["nav.auditTrail"] = "Rekodi ya Ukaguzi",
            ["nav.settings"] = "Mipangilio",

            ["common.search"] = "Tafuta",
            ["common.filter"] = "Chuja",
            ["common.clear"] = "Futa",
            ["common.save"] = "Hifadhi",
            ["common.cancel"] = "Ghairi",
            ["common.delete"] = "Futa",
            ["common.edit"] = "Hariri",
            ["common.view"] = "Tazama",
            ["common.create"] = "Unda",
            ["common.loading"] = "Inapakia...",
            ["common.noData"] = "Hakuna data",
            ["common.actions"] = "Vitendo",
            ["common.status"] = "Hali",
            ["common.date"] = "Tarehe",
            ["common.amount"] = "Kiasi",
            ["common.total"] = "Jumla",
            ["common.logout"] = "Ondoka",
            ["common.login"] = "Ingia",
            ["common.welcome"] = "Karibu tena",

            ["invoice.docNum"] = "Nambari ya Hati",
            ["invoice.cardCode"] = "Msimbo wa Mteja",
            ["invoice.cardName"] = "Jina la Mteja",
            ["invoice.docDate"] = "Tarehe ya Hati",
            ["invoice.dueDate"] = "Tarehe ya Malipo",
            ["invoice.docTotal"] = "Jumla",
            ["invoice.createNew"] = "Tengeneza Ankara Mpya",

            ["product.itemCode"] = "Msimbo wa Bidhaa",
            ["product.itemName"] = "Jina la Bidhaa",
            ["product.quantity"] = "Idadi",
            ["product.price"] = "Bei",
            ["product.stock"] = "Stoki",

            ["msg.success"] = "Operesheni imefanikiwa",
            ["msg.error"] = "Kosa limetokea",
            ["msg.confirm"] = "Una uhakika?",
            ["msg.saved"] = "Mabadiliko yamehifadhiwa",
            ["msg.deleted"] = "Kipengee kimefutwa",

            ["theme.light"] = "Hali ya Mwanga",
            ["theme.dark"] = "Hali ya Giza",
            ["theme.toggle"] = "Badilisha Mandhari",

            ["search.placeholder"] = "Tafuta ankara, bidhaa, wateja...",
            ["search.noResults"] = "Hakuna matokeo",
            ["search.hint"] = "Bonyeza Ctrl+K kutafuta"
        }
    };

    public LocalizationService(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task<string> GetLanguageAsync()
    {
        try
        {
            var language = await _localStorage.GetItemAsync<string>(LanguageKey);
            _currentLanguage = language ?? "en";
            return _currentLanguage;
        }
        catch
        {
            return "en";
        }
    }

    public async Task SetLanguageAsync(string languageCode)
    {
        if (SupportedLanguages.Any(l => l.Code == languageCode))
        {
            _currentLanguage = languageCode;
            await _localStorage.SetItemAsync(LanguageKey, languageCode);
            OnLanguageChanged?.Invoke();
        }
    }

    public string Translate(string key)
    {
        if (_translations.TryGetValue(_currentLanguage, out var langDict))
        {
            if (langDict.TryGetValue(key, out var translation))
                return translation;
        }

        // Fallback to English
        if (_translations.TryGetValue("en", out var enDict))
        {
            if (enDict.TryGetValue(key, out var translation))
                return translation;
        }

        return key; // Return key if no translation found
    }

    public string Translate(string key, params object[] args)
    {
        var template = Translate(key);
        return string.Format(template, args);
    }
}
