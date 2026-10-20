using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class FactoryTransfer
    {
        public static readonly Error SapDisabled =
            Error.Failure("FactoryTransfer.SapDisabled", "SAP integration is disabled, so no stock can be moved.");

        public static Error NotFound(int id) =>
            Error.NotFound("FactoryTransfer.NotFound", $"Factory transfer {id} was not found.");

        public static readonly Error UserNotFound =
            Error.NotFound("FactoryTransfer.UserNotFound", "Your account could not be found or is inactive.");

        public static Error NotAFactoryWarehouse(string warehouse) =>
            Error.Validation("FactoryTransfer.NotAFactoryWarehouse",
                $"{warehouse} is not a production or dispatch warehouse, so it cannot send a factory transfer.");

        public static Error SourceNotAssigned(string warehouse) =>
            Error.Forbidden("FactoryTransfer.SourceNotAssigned",
                $"{warehouse} is not one of your warehouses, so you cannot send stock from it.");

        public static readonly Error SendNotPermitted =
            Error.Forbidden("FactoryTransfer.SendNotPermitted", "Your account may not send factory transfers.");

        public static readonly Error AcceptNotPermitted =
            Error.Forbidden("FactoryTransfer.AcceptNotPermitted", "Your account may not accept factory transfers.");

        public static Error DestinationNotAssigned(string warehouse) =>
            Error.Forbidden("FactoryTransfer.DestinationNotAssigned",
                $"This transfer is for {warehouse}, which is not one of your warehouses, so you cannot accept or reject it.");

        public static Error InvalidDestination(string destination, string department) =>
            Error.Validation("FactoryTransfer.InvalidDestination",
                $"{destination} is not a {department} warehouse on the other side of the factory.");

        public static readonly Error CannotDecideOwnTransfer =
            Error.Forbidden("FactoryTransfer.CannotDecideOwnTransfer",
                "You sent this transfer, so somebody on the receiving side has to accept or reject it.");

        public static readonly Error OnlySenderMayRecall =
            Error.Forbidden("FactoryTransfer.OnlySenderMayRecall", "Only the person who sent this transfer may recall it.");

        public static readonly Error OnlySenderMayCancel =
            Error.Forbidden("FactoryTransfer.OnlySenderMayCancel", "Only the person who sent this transfer may cancel it.");

        public static readonly Error RetryNotPermitted =
            Error.Forbidden("FactoryTransfer.RetryNotPermitted",
                "Only the sender, the person who decided it, or somebody who may accept it can retry this transfer.");

        public static Error NotActionable(string status) =>
            Error.Conflict("FactoryTransfer.NotActionable", $"This transfer is {status} and cannot be changed that way.");

        public static readonly Error DuplicateRequestFromAnotherUser =
            Error.Conflict("FactoryTransfer.DuplicateRequest", "This request id has already been used by another account.");

        public static Error LinesMismatch(string message) =>
            Error.Validation("FactoryTransfer.LinesMismatch", message);

        public static readonly Error ShortAcceptNeedsReason =
            Error.Validation("FactoryTransfer.ShortAcceptNeedsReason",
                "Say why less than was sent is being accepted. The rest goes back to the sender.");

        public static readonly Error NothingAccepted =
            Error.Validation("FactoryTransfer.NothingAccepted",
                "Every accepted quantity is zero. Reject the transfer instead, with the reason.");

        public static readonly Error PostInProgress =
            Error.Conflict("FactoryTransfer.PostInProgress",
                "This transfer is being posted to SAP right now. Wait for it to finish, then refresh.");

        public static Error InsufficientStock(string message) =>
            Error.Validation("FactoryTransfer.InsufficientStock", message);

        public static Error PostFailed(string message) =>
            Error.Failure("FactoryTransfer.PostFailed", message);

        public static readonly Error DeliverNotPermitted =
            Error.Forbidden("FactoryTransfer.DeliverNotPermitted", "Your account may not load deliveries for the depots.");

        public static Error NotADispatchWarehouse(string warehouse) =>
            Error.Validation("FactoryTransfer.NotADispatchWarehouse",
                $"{warehouse} is not a dispatch warehouse, so a depot truck cannot be loaded from it.");

        public static readonly Error TruckDetailsRequired =
            Error.Validation("FactoryTransfer.TruckDetailsRequired",
                "Give the truck's registration and the driver's name. The depot checks both when it arrives.");

        public static readonly Error LineSourceNotAllowed =
            Error.Validation("FactoryTransfer.LineSourceNotAllowed",
                "Only a depot delivery loads lines from different warehouses; every other transfer sends from one.");

        public static Error DepotOrderNotFound(int docEntry) =>
            Error.NotFound("FactoryTransfer.DepotOrderNotFound", $"Depot order {docEntry} was not found in SAP.");

        public static Error DepotOrderNotOpen(int docNum) =>
            Error.Conflict("FactoryTransfer.DepotOrderNotOpen", $"Depot order #{docNum} is already closed in SAP.");

        public static Error DepotOrderForAnotherDepot(int docNum, string depot) =>
            Error.Validation("FactoryTransfer.DepotOrderForAnotherDepot", $"Depot order #{docNum} is for {depot}, not this truck's depot.");

        public static Error NotADepot(string warehouse) =>
            Error.Validation("FactoryTransfer.NotADepot", $"{warehouse} is not a depot this module delivers to.");

        public static readonly Error CannotCancelPosted =
            Error.Conflict("FactoryTransfer.CannotCancelPosted",
                "The stock may already have left in SAP, so this transfer cannot be cancelled. Retry it so it is found and finished.");
    }
}
