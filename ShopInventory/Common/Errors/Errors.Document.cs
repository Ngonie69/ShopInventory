using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Document
    {
        public static Error TemplateNotFound(int id) =>
            Error.NotFound("Document.TemplateNotFound", $"Document template with ID {id} not found");

        public static Error DefaultTemplateNotFound(string documentType) =>
            Error.NotFound("Document.DefaultTemplateNotFound", $"No default template found for document type '{documentType}'");

        public static Error GenerationFailed(string message) =>
            Error.Failure("Document.GenerationFailed", message);

        public static Error AttachmentNotFound(int id) =>
            Error.NotFound("Document.AttachmentNotFound", $"Attachment with ID {id} not found");

        public static Error SignatureNotFound(int id) =>
            Error.NotFound("Document.SignatureNotFound", $"Signature with ID {id} not found");

        public static Error EmailTemplateNotFound(string code) =>
            Error.NotFound("Document.EmailTemplateNotFound", $"Email template with code '{code}' not found");

        public static Error EmailTemplateFailed(string message) =>
            Error.Failure("Document.EmailTemplateFailed", message);

        public static Error UploadFailed(string message) =>
            Error.Failure("Document.UploadFailed", message);
    }
}
