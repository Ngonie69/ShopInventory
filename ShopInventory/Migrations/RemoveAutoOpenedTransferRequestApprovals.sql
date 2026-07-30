-- Cleanup: approval requests that listing opened against SAP-raised transfer requests
-- Date: 2026-07-30
-- Description: Listing the Transfer Requests tab used to open an approval request for every request
--              it showed, including ones raised directly in SAP. A SAP-raised request has no
--              originator to route on, so each one landed on the catch-all "Administrator Review"
--              stage and the tab reported it as "Awaiting Administrator Review".
--
--              Listing no longer opens approvals (InventoryTransferApprovalService.EnrichAsync), so
--              no new ones appear. This removes the ones already opened and stops their authorizer
--              notifications nagging.
--
--              Only untouched stubs qualify: no originator recorded, no decision taken, still
--              pending, and nothing generated from it. Anything a person has acted on is left alone,
--              as is every approval raised through the app — those carry an originator.
--
-- Run against the API database. Review the first result set before committing.

BEGIN;

-- Preview: the approvals about to be removed.
SELECT r."DocumentNumber", r."DocumentKey", r."FromWarehouse", r."ToWarehouse", r."CreatedAtUtc"
FROM "ApprovalRequests" r
WHERE r."DocumentType" = 'InventoryTransferRequest'
  AND r."OriginatorUserId" IS NULL
  AND r."OriginatorName" = 'Unknown'
  AND r."Status" = 'Pending'
  AND r."GeneratedDocumentEntry" IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM "ApprovalDecisions" d WHERE d."ApprovalRequestId" = r."Id"
  )
ORDER BY r."CreatedAtUtc";

-- Remove them, and mark read the review notifications they raised. Notification ids for transfer
-- requests are "<DocEntry>:<StageId>", so the DocEntry is the part before the first colon.
WITH removed AS (
    DELETE FROM "ApprovalRequests" r
    WHERE r."DocumentType" = 'InventoryTransferRequest'
      AND r."OriginatorUserId" IS NULL
      AND r."OriginatorName" = 'Unknown'
      AND r."Status" = 'Pending'
      AND r."GeneratedDocumentEntry" IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM "ApprovalDecisions" d WHERE d."ApprovalRequestId" = r."Id"
      )
    RETURNING r."DocumentKey"
)
UPDATE "Notifications" n
SET "IsRead" = TRUE,
    "ReadAt" = (NOW() AT TIME ZONE 'UTC')
WHERE n."Category" = 'TransferApproval'
  AND n."EntityType" = 'TransferRequestApproval'
  AND n."IsRead" = FALSE
  AND split_part(n."EntityId", ':', 1) IN (SELECT "DocumentKey" FROM removed);

-- COMMIT;
-- ROLLBACK;
