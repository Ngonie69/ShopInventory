using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Backup
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Backup.NotFound", $"Backup with ID {id} not found");

        public static Error CreationFailed(string message) =>
            Error.Failure("Backup.CreationFailed", message);

        public static Error RestoreFailed(string message) =>
            Error.Failure("Backup.RestoreFailed", message);

        public static Error DownloadFailed(string message) =>
            Error.Failure("Backup.DownloadFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("Backup.DeleteFailed", message);

        public static Error ResetFailed(string message) =>
            Error.Failure("Backup.ResetFailed", message);
    }
}
