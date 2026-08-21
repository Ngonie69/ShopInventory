# Fiscalisation: the van handset contract

Date: 2026-08-18
Audience: the team that owns **KefalosVanSales** (MAUI Android, `SIS Vansales/KefalosVanSales`).
Scope: everything the handset must do to produce ZIMRA-valid receipts offline and deliver them, stated so
it can be implemented without reading the ShopInventory API or the Fiscalisation platform.

Three codebases have to agree here:

| Repo | Role |
| --- | --- |
| `KefalosVanSales` | Signs receipts with a key in the Android Keystore. Prints. Queues. Uploads. |
| `ShopInventory` (the API) | Issues the lease, takes custody of the batch, posts to SAP, forwards the signed receipt on. |
| `Fiscalisation` (`fiscal.kefaloscheese.com`) | Re-derives the signed payload, verifies the signature, archives the receipt, packages the fiscal day for ZIMRA. |

Every non-obvious claim below cites the file it comes from. Paths are repo-relative and prefixed with the
repo name. Where the source and the intended design differ today, that is stated as a gap rather than
smoothed over — see §2, §4, §6 and §7, and the **Open questions** at the end.

---

## 1. Why the handset signs and the server posts

A ZIMRA fiscal device is **one hash-chained sequence of receipts**. Receipt N carries the hash of receipt
N−1, and the whole fiscal day is uploaded to FDMS as a single file at day close. If two writers both sign
into the same device's chain, they each produce a different "receipt N", the chain forks, and FDMS refuses
the entire fiscal day when the file is uploaded — not the two bad receipts, the day.

That constraint plus the Android Keystore produces the whole architecture:

- The handset's private key is generated inside the Keystore and is **non-exportable**. Signing happens in
  the Keystore and only the signature comes back
  (`KefalosVanSales/Services/Fiscal/AndroidDeviceSigningKey.cs:13-24`).
- Therefore the platform **cannot** sign for that device. It holds no copy of the key and never will.
- Therefore **every** receipt for that device must go through the pre-signed ingest path
  (`POST /api/receipts/ingest-signed`), and **none** through the server-signed path
  (`POST /api/receipts/submit`).
- Mixing them forks the chain. The online van-sales invoice path used to do exactly that, sending
  `Fiscalize = true` on every sale; it now sends it only when the handset stamped nothing, so a handset
  that owns a device is the only writer on its chain
  (`ShopInventory/Features/VanSalesCompatibility/VanSalesCompatibilityMapper.cs`, `MapInvoiceRequest`).
  See §6 for the contract both paths now share.

So the division of labour is fixed:

| | Handset | Server |
| --- | --- | --- |
| Assigns the receipt sequence | yes, from the lease | no |
| Assigns the receipt date | yes | no — assigning one would change the payload out from under the signature (`Fiscalisation/src/Fiscalisation.Web/Endpoints/FiscalIntegrationEndpoints.IngestSigned.cs:52-60`) |
| Computes line totals, tax block, total | yes, and must match the server exactly | yes, re-derived from the lines it receives |
| Signs | yes | never, for a handset-owned device |
| Posts to SAP | no | yes, at end of day |
| Delivers the receipt to ZIMRA | no | yes, via the platform's offline file |

The handset never contacts the Fiscalisation platform directly. It talks only to the ShopInventory API,
which forwards the receipt verbatim
(`ShopInventory/Services/VanSalesSignedReceiptIngestService.cs:446-550`).

**The single registration rule.** One ZIMRA device per handset, never shared. The lease hands out sequence
positions on the understanding that the holder is the sole writer of that chain; the server cannot enforce
it (`ShopInventory/DTOs/VanSalesFiscalLeaseDto.cs:5-13`). The API side of this is the offline-signing
nomination: exactly one user is nominated per device, and a lease request from anyone else is refused
(`ShopInventory/Features/VanSalesCompatibility/Queries/GetVanSalesFiscalLease/GetVanSalesFiscalLeaseHandler.cs:77-98`).

---

## 2. Provisioning — the blocking gap

**Nothing in the app ever creates a signing key.** Verified by grep over
`C:/Users/ngoni/source/repos/SIS Vansales/KefalosVanSales`, on `--include=*.cs --include=*.xaml`:

| Grep | Result |
| --- | --- |
| `EnsureCreated` | one hit, the declaration at `Services/Fiscal/AndroidDeviceSigningKey.cs:55`. No callers. |
| `CreateCertificateSigningRequestPem` | one hit, the declaration at `Services/Fiscal/AndroidDeviceSigningKey.cs:111`. No callers. |
| `ExportPublicKey` | two hits — the declaration at `:135` and the call at `:118`, inside the uncalled `CreateCertificateSigningRequestPem`. |
| `AndroidDeviceSigningKey` | the class, its constructor, its nested `KeystoreSignatureGenerator`, and one DI registration at `MauiProgram.cs:131`. |

(The same grep matches copies under `.claude/worktrees/*`; those are worktrees of this repo, not separate
call sites.)

The consequence chain:

1. No key is ever generated in the Keystore.
2. So no CSR can be produced — `CreateCertificateSigningRequestPem` calls `ExportPublicKey`, which throws
   when the alias holds no certificate (`AndroidDeviceSigningKey.cs:139-141`).
3. So no device can be registered with ZIMRA in Offline mode.
4. So the **first offline sale throws** at `AndroidDeviceSigningKey.cs:86-89` —
   `OpenKeyStore().GetKey(_alias, null)` returns null and `SignData` raises *"This handset holds no fiscal
   signing key, so it cannot sign a receipt. It has not been registered as a fiscal device."* — from inside
   `ReceiptSigner.Sign`, which `OfflineSaleCaptureService.CaptureOnceAsync` calls at
   `Services/OfflineSaleCaptureService.cs:251`, i.e. at the till, with a customer waiting.

`EnsureCreated` is deliberately not called on demand: *"a key created mid-sale would be unregistered, and
every receipt signed with it refused"* (`AndroidDeviceSigningKey.cs:51-54`).

### What has to be built

**2.1 Call `EnsureCreated()` once, at provisioning, not at the till.**
A one-time admin action on a handset being commissioned. It is idempotent — it returns immediately if the
alias exists (`AndroidDeviceSigningKey.cs:59-62`) — but it must not be wired into the sale path, because a
key generated after registration is a key ZIMRA has never seen.

**2.2 Surface the CSR PEM on an admin screen so it can be copied off.**
`CreateCertificateSigningRequestPem(subjectName)` returns a PKCS#10 CSR in PEM, signed by the Keystore key
itself, which is what proves the device holds the private half without exposing it
(`AndroidDeviceSigningKey.cs:102-132`).

- `subjectName` is an X.500 DN, e.g. `CN=ZIMRA-VAN006-0000035410` (`AndroidDeviceSigningKey.cs:108-110`).
  The exact DN ZIMRA expects per device is an operations input — see Open questions.
- The screen needs: generate, display, copy-to-clipboard, and share. The PEM is a few hundred characters;
  it does not need to be transmitted by the app.
- The key is bound to this handset forever. It cannot be backed up, moved, or recovered. A wiped or
  replaced handset needs a new key, a new CSR and a fresh ZIMRA registration
  (`AndroidDeviceSigningKey.cs:22-23`).

**2.3 Register the CSR with the platform.**

```
POST https://fiscal.kefaloscheese.com/api/device-registration/register
{ "deviceId": <int>, "activationKey": "<8 digits>", "certificateRequest": "<CSR PEM>" }
→ 200 { "operationID": "...", "certificate": "..." }
```

`Fiscalisation/src/Fiscalisation.Web/Endpoints/FiscalIntegrationEndpoints.DeviceManagement.cs:86-129`.

Validation, from `Fiscalisation/src/Fiscalisation.Application/Commands/RegisterDevice/RegisterDeviceCommandValidator.cs:21-41`:

- `deviceId` > 0.
- `activationKey` — **exactly 8 digits**, no other characters. Issued by ZIMRA per device.
- `certificateRequest` — must start with `-----BEGIN CERTIFICATE REQUEST-----` or
  `-----BEGIN NEW CERTIFICATE REQUEST-----` and end with the matching `-----END …-----`. The validator's
  message names this as DEV03 prevention.

**This endpoint is not callable from the handset.** It sits on the platform's `adminApiGroup`, which
requires an authenticated console session plus the `Device.Setup` permission
(`FiscalIntegrationEndpoints.cs:79-83`, `FiscalIntegrationEndpoints.DeviceManagement.cs:126`) — not an API
key. The operator pastes the CSR into the Fiscalisation console. The handset's job ends at producing and
displaying the PEM.

**2.4 Fail closed at app start, not at the point of sale.**
On startup (and on entering any sale flow), check `AndroidDeviceSigningKey.Exists`
(`AndroidDeviceSigningKey.cs:39-49`) and, if false, disable offline capture with a clear message naming
provisioning as the remedy. Today the only signal is an `InvalidOperationException` thrown mid-capture with
a printed receipt already expected.

`OfflineSalePolicy.Evaluate` already refuses on every *lease* condition
(`Services/Fiscal/OfflineSalePolicy.cs:24-131`) but has no key check — the lease can be perfectly valid on a
handset that holds no key. Add the key check there, or ahead of it.

---

## 3. The canonical signed payload

This is the core of the contract. Two independent implementations already exist and must agree **to the
character**:

- `Fiscalisation/src/Fiscalisation.Domain/Signatures/ReceiptCanonicalPayload.cs` — the platform's.
- `KefalosVanSales/Services/Fiscal/ReceiptCanonicalPayload.cs` — the handset's port.

A divergence does not corrupt anything visibly. The receipt prints, the QR scans, the customer leaves — and
ZIMRA refuses the whole fiscal day when the offline file is uploaded, hours later
(`ReceiptCanonicalPayload.cs:8-21` in the Fiscalisation repo).

### 3.1 The derivation the client must mirror

The client does **not** sign over its own totals. It signs over the values the server will re-derive from
the lines it sends. If the two derivations differ by one cent, the signature fails verification.

The server's derivation lives in
`Fiscalisation/src/Fiscalisation.Web/Endpoints/FiscalIntegrationEndpoints.ReceiptBuilder.cs:20-65`, and is
shared by `/api/receipts/submit` and `/api/receipts/ingest-signed` deliberately, so the verifier and the
signer cannot drift apart (`:12-15`).

**Step 1 — line total, per line.**

```
lineTotal = round(price × quantity, 2, half away from zero)
```

`ReceiptBuilder.cs:35`. `price` is the **tax-inclusive unit price** as sent on the wire, already rounded to
2dp by the client — not a list price, not a net price
(`ShopInventory/DTOs/VanSalesOfflineSaleRequest.cs:156-161, 174-175`,
`KefalosVanSales/Services/Fiscal/OfflineSaleWireMapper.cs:44-46`).

The handset's composer derives the line total from the **rounded** inclusive unit price, so the printed
arithmetic adds up as the customer reads it (`FiscalReceiptComposer.cs:89-93`). The server does the same
thing to the same number. They agree only because the client rounds the unit price before sending it — send
an unrounded price and the two `round(price × qty)` results can differ.

`MidpointRounding.AwayFromZero` throughout. .NET's default is banker's rounding (`ToEven`), which
disagrees on exactly the half-cent values a 15%-inclusive extraction produces all day, and over many lines
drifts far enough from the payment total to trigger FDMS RCPT039 (`ReceiptBuilder.cs:32-34`).

**Step 2 — group into taxes.**

Group the lines by the triple **(taxId, taxPercent, taxCode)** (`ReceiptBuilder.cs:41`). The `taxCode` used
in the grouping key is the wire value **trimmed**, with blank/whitespace normalised to `null`
(`ReceiptBuilder.cs:38`). It is *not* upper-cased at grouping time — only later, when the canonical string
is built (§3.2). The practical rule for the client: **send `tax_code` already trimmed and upper-cased on
every line**, so grouping and canonicalisation cannot disagree.

Per group:

```
salesTotal = Σ lineTotal over the group

taxAmount  = round(salesTotal × p / (100 + p), 2, away from zero)   when taxPercent = p > 0   (tax-inclusive)
           = 0                                                      when taxPercent is null or ≤ 0

salesAmountWithTax = salesTotal                                     (tax-inclusive)
```

`ReceiptBuilder.cs:44-60`. Van sales are always tax-inclusive: the ShopInventory forwarder hard-sets
`TaxInclusive = true` on the ingest request
(`ShopInventory/Services/VanSalesSignedReceiptIngestService.cs:511-513`). The exclusive branch in
`ReceiptBuilder.cs:47-48` and `:57-59` therefore never applies to a van receipt — do not implement it.

**Step 3 — receipt total.**

```
receiptTotal = Σ lineTotal over all lines        (tax-inclusive)
```

`ReceiptBuilder.cs:63-65`. Note this is the sum of *line* totals, not the sum of `salesAmountWithTax` — in
the tax-inclusive case they are equal by construction, but the server takes the former, so take the former.

The payment amount is anchored to this same computed total, never to an upstream figure
(`ReceiptBuilder.cs:118-121`).

> **Divergence to close on the handset.** `FiscalReceiptComposer.SummariseTaxes` currently groups by
> `TaxID` alone and takes the code from `group.First()`
> (`KefalosVanSales/Services/Fiscal/FiscalReceiptComposer.cs:127-148`). The server groups by the triple.
> Today they coincide because the lease supplies exactly one `(percent, code)` per `tax_id` and the
> composer reads both from the lease (`:85-87`, `:104-106`). They stop coinciding the moment a lease
> carries two entries for one `tax_id` — which the lease DTO permits, and which a mid-day rate change is
> the natural cause of. Change the composer to group by the same triple.

### 3.2 The canonical string

Concatenate, with **no separators**, in exactly this order
(`Fiscalisation/src/Fiscalisation.Domain/Signatures/ReceiptCanonicalPayload.cs:45-64`):

| # | Component | Formatting rule |
| --- | --- | --- |
| 1 | `deviceId` | Decimal integer, invariant culture. |
| 2 | `receiptType` | The **upper-cased enum name**, not the numeric value: `FISCALINVOICE`, `CREDITNOTE`, `DEBITNOTE`. A van sale is always `FISCALINVOICE`. |
| 3 | `receiptCurrency` | Trimmed, upper-cased. Null becomes empty. |
| 4 | `receiptGlobalNo` | Decimal integer, invariant culture. |
| 5 | `receiptDate` | `yyyy-MM-ddTHH:mm:ss`, invariant culture. **Local wall clock, no offset, no fractional seconds.** |
| 6 | `receiptTotal` | **Whole cents**, see below. |
| 7 | tax block | See below. May be empty only if there are no taxes, which cannot happen for a van sale. |
| 8 | `previousReceiptHash` | Appended as the **base64 text**, trimmed. Omitted entirely when null, empty or whitespace. |

**`receiptCounter` is NOT in the payload.** It is carried on the wire and checked by the platform against
its archive, but it contributes nothing to the signed string. Adding it is a silent, total break.

**Amounts in cents** (`ReceiptCanonicalPayload.cs:157-161`):

```
cents = decimal.Round(amount × 100, 0, MidpointRounding.AwayFromZero)
output = cents.ToString("0", InvariantCulture)
```

No decimal point, no thousands separator, no sign for positives, a leading `-` for negatives. Use a decimal
type, never a binary float: `123.445` is not representable in `double` and the hash would differ from the
server's for amounts that look identical on screen
(`KefalosVanSales/Services/Fiscal/FiscalReceipt.cs:6-14`).

**Tax percent** (`ReceiptCanonicalPayload.cs:147-150`):

| Value | Contributes |
| --- | --- |
| `null` | `""` — nothing at all |
| `0` | `"0.00"` |
| `15` | `"15.00"` |

Null and zero are **different**. Null is untaxed, zero is zero-rated, and collapsing them makes a
zero-rated line and an untaxed line sign identically. Format is always `0.00`, invariant culture.

**The tax block** (`ReceiptCanonicalPayload.cs:122-139`):

Sort the tax entries by `taxId` ascending, then by the **normalised tax code** using an **ordinal**
comparison — not a culture-sensitive one, or two devices with different locales order the same taxes
differently. Normalised means: null/blank → `""`, otherwise trimmed and upper-cased
(`ReceiptCanonicalPayload.cs:141-144`).

For each entry in that order, append four fields with no separators:

```
normalisedTaxCode  +  formattedTaxPercent  +  centsOf(taxAmount)  +  centsOf(salesAmountWithTax)
```

The order the taxes arrive in from the caller is irrelevant — the sort makes the payload
order-independent, and there is a test pinning that
(`Fiscalisation/tests/Fiscalisation.Application.Tests/Signatures/ReceiptCanonicalPayloadTests.cs:110-120`).

**The receipt date, again, because it is the most common way to get this wrong.** Local wall clock. Not
UTC, not an offset-bearing timestamp. FDMS compares the receipt date against the fiscal day it belongs to,
both stated in the taxpayer's own time; a UTC value shifts receipts across the day boundary (RCPT041) in
either direction depending on the hour (`ReceiptCanonicalPayload.cs:25-30`). Truncate to the second
*before* signing — the format drops sub-second components, so two receipts in the same second sign the same
date, and the sequence has to keep them apart some other way
(`ReceiptCanonicalPayloadTests.cs:149-163`; the handset truncates at
`FiscalReceiptComposer.cs:164-165`).

### 3.3 Hashing and signing

`KefalosVanSales/Services/Fiscal/ReceiptSigner.cs:58-67`:

```
payloadBytes = UTF-8 bytes of the canonical string, no BOM
hash         = base64( SHA-256( payloadBytes ) )
signature    = base64( RSASSA-PKCS1-v1_5 over SHA-256 of payloadBytes )
```

- **PKCS#1 v1.5, not PSS.** The platform verifies with `RSASignaturePadding.Pkcs1` and a PSS signature
  simply fails to verify, with no error naming padding as the cause
  (`ReceiptSigner.cs:16-21`, `AndroidDeviceSigningKey.cs:66-68`).
- Java's algorithm name is `SHA256withRSA` (`AndroidDeviceSigningKey.cs:29`).
- Key: RSA 2048, `KeyStorePurpose.Sign`, digest SHA-256, padding `RSA_PKCS1`
  (`AndroidDeviceSigningKey.cs:64-70`).
- Base64 is standard, with padding — not base64url.
- The **hash** is what the next receipt in the chain is signed against. The **signature** is what the QR
  verification code is derived from (`Services/Fiscal/FiscalQrPayloadBuilder.cs:21-24, 52-73`). Using one
  where the other belongs produces a plausible-looking value that verifies against nothing.

Sign only after the sequence and date are assigned. `ReceiptSigner.Sign` refuses otherwise
(`ReceiptSigner.cs:45-56`) — all three are part of the payload, so a sequence assigned afterwards
invalidates the signature.

### 3.4 Golden vectors — the self-test that pins both sides

From `Fiscalisation/tests/Fiscalisation.Application.Tests/Signatures/ReceiptCanonicalPayloadTests.cs`.

**These are already ported.** The identical literals — payload, hash, rounding table, tax-percent table,
order independence, sub-second truncation — live in
`KefalosVanSales.Tests/Fiscal/ReceiptCanonicalPayloadTests.cs`, against the handset's own
`ReceiptCanonicalPayload`. They are reproduced below so a divergence can be localised by reading rather
than by bisecting, and so the handset team can check the two sets still match without opening the other
repository.

**If one of these fails, do not update the literal to match your code** — work out which side moved. A
genuine algorithm change has to land in both repositories in the same breath, with the vectors
regenerated in both; updating one side alone leaves both suites green and every receipt broken in the
field (`:20-22`).

**Input** (`:26-68`):

| Field | Value |
| --- | --- |
| `deviceId` | `12345` |
| `receiptType` | `FiscalInvoice` |
| `receiptCurrency` | `" usd "` — note the surrounding spaces and lower case |
| `receiptGlobalNo` | `4321` |
| `receiptCounter` | `7` — present, and deliberately absent from the payload |
| `receiptDate` | `2026-08-10 14:30:05`, kind `Unspecified` |
| `receiptTotal` | `123.445` |
| tax entry A (supplied **first**) | `taxId 3`, `taxCode " c "`, `taxPercent 15.00`, `taxAmount 16.11`, `salesAmountWithTax 123.445` |
| tax entry B (supplied second) | `taxId 1`, `taxCode "a"`, `taxPercent null`, `taxAmount 0`, `salesAmountWithTax 10.00` |
| `previousReceiptHash` | `cHJldmlvdXMtaGFzaA==` (base64 of `previous-hash`) |

**Expected canonical payload** (`:31-32`):

```
12345FISCALINVOICEUSD43212026-08-10T14:30:0512345A01000C15.00161112345cHJldmlvdXMtaGFzaA==
```

Decomposed, so a mismatch can be localised:

| Segment | Source |
| --- | --- |
| `12345` | deviceId |
| `FISCALINVOICE` | receipt type, upper-cased enum name |
| `USD` | currency, trimmed and upper-cased from `" usd "` |
| `4321` | global no |
| `2026-08-10T14:30:05` | receipt date, local wall clock |
| `12345` | total — `123.445` → **12345** cents, half away from zero |
| `A` + `` + `0` + `1000` | tax id **1** first (sorted): code `A`, percent `null` → **empty string**, tax `0` → `0`, sales `10.00` → `1000` |
| `C` `15.00` `1611` `12345` | tax id 3: code `C` from `" c "`, percent `15.00`, tax `16.11` → `1611`, sales `123.445` → `12345` |
| `cHJldmlvdXMtaGFzaA==` | previous hash, appended as base64 **text** |

**Expected base64 SHA-256 of that payload** (`:34`):

```
K9HAC/UQ/781L6VzhHVEb4llGrgSzm04UycPD09TNVk=
```

**With no previous hash** — `null`, `""` and `"   "` must all behave identically, or a client that sends an
empty string signs a different payload than the server verifies (`:87-103`):

```
12345FISCALINVOICEUSD43212026-08-10T14:30:0512345A01000C15.00161112345
```

**Cents rounding** (`:127-135`) — the case banker's rounding gets wrong is the first one:

| Amount | Expected | Banker's rounding would give |
| --- | --- | --- |
| `123.445` | `12345` | `12344` ← wrong |
| `123.455` | `12346` | |
| `-1.005` | `-101` | |
| `0` | `0` | |

**Tax percent** (`:141-147`): `null` → `""`, `0m` → `"0.00"`, `15m` → `"15.00"`.

**Order independence** (`:110-120`): reversing the tax list must produce the identical payload.

**Sub-second precision** (`:154-163`): a receipt date of `14:30:05.999` must produce the identical payload
to `14:30:05`.

---

## 4. Idempotency, strictly enforced

`van_order` is the idempotency key end to end: unique on the handset's local receipts table, unique on
`DesktopSaleEntity.ExternalReferenceId` in the API, and written to `U_Van_saleorder` in SAP so the
end-of-day mop-up cannot post the same sale twice
(`ShopInventory/DTOs/VanSalesOfflineSaleRequest.cs:25-32`,
`ShopInventory/Models/Entities/DesktopSaleEntity.cs:83`).

### 4.1 The rule

**Generate `van_order` once per cart. Never regenerate it for the same cart.**

Format: `BRANCH-TYPE-YYYYMMDD-XXXXXX`, e.g. `VAN006-INV-20260810-D261C8`. Composed with an invariant date
stamp and a 6-hex-character suffix (`KefalosVanSales/Services/VanOrderReference.cs:37-50`).

The reference is derived from **what is being sent**, not from when the send happens. `PendingVanOrder`
already implements the rule and it must be used on every submission path
(`KefalosVanSales/Services/PendingVanOrder.cs:51-72`):

1. Fingerprint the submission — branch, type, customer, and every line's code and quantity, ordered by
   code, trimmed and upper-cased (`VanOrderReference.FingerprintCart`, `:58-84`). A conversion of an
   existing document fingerprints the source document instead (`FingerprintDocument`, `:91-101`).
2. If a reference is stored against that same fingerprint, reuse it. Otherwise mint a new one and store
   both.
3. Clear the slot once the submission has succeeded, or once the rep abandons it. A reference left behind
   is reused for a different document and the server refuses the second one as a duplicate of the first
   (`PendingVanOrder.cs:74-89`).

**It must survive app restart.** It does — `PendingVanOrder` stores into `Preferences`, which is exactly the
case that matters: a rep whose handset dies mid-post reopens the app, resubmits the same cart, and the
server recognises the reference it already has (`PendingVanOrder.cs:6-10`).

There is **one slot per submitting screen**, not one per document type
(`PendingVanOrder.Slot` = `Invoice` | `SalesOrder` | `Conversion`, `:13-33`). A rep can leave an unsent cart
on one screen and start a conversion on another and both produce an `INV`; keyed by type alone they would
overwrite each other's fingerprint and both mint fresh references — the exact bug the class exists to
prevent.

### 4.2 The in-memory set is not enough

`Views/SalesCartSummary.xaml.cs:28`, `Views/SOCart.xaml.cs:12` and `Views/ConvertToInvoice.xaml.cs:30` each
hold a `private static readonly HashSet<string> _submittedVanOrders`. That guard is per-process and
per-session: it is empty after every app restart, and it says nothing about a request that reached the
server and whose reply was lost. It stops a double tap. It does not stop a duplicate document. The durable
guards are `PendingVanOrder` on the handset and the unique index on the server.

### 4.3 The batch upload path

`POST /api/vansales/sales` reports **per sale**, not per batch
(`ShopInventory/Controllers/VanSalesCompatibilityController.cs:432-449`). Each result is `accepted`,
`duplicate` or `rejected`:

| Status | Meaning | Handset action |
| --- | --- | --- |
| `accepted` | Stored and held for end-of-day posting. | Delete the queued copy. |
| `duplicate` | Already received on a previous attempt. **This is a success.** | Delete the queued copy. |
| `rejected` | Refused on content; nothing was stored. | Keep it, quarantine it, surface it to a person. |

`ShopInventory/DTOs/VanSalesOfflineSaleRequest.cs:214-232` and
`ShopInventory/Features/VanSalesCompatibility/Commands/IngestVanSalesOfflineSales/IngestVanSalesOfflineSalesHandler.cs:184-195`.
A sale not mentioned in the reply at all stays queued. The current uploader implements this correctly
(`KefalosVanSales/Services/OfflineSalesUploadService.cs:194-213`).

The server also de-duplicates **within** one batch, so the same reference appearing twice in one upload is
answered as a duplicate rather than failing the whole batch on the unique index
(`IngestVanSalesOfflineSalesHandler.cs:167-195`).

`van_order` is mandatory and rejected when blank, with the message *"van_order is required — it is the
idempotency key."* (`:175-182`).

### 4.4 The `Idempotency-Key` header — target state, and why not yet

**Target state:** the handset sends `van_order` in an `Idempotency-Key` request header **as well as** in the
body, and a repeat under the same key returns the **original response body**, not an error.

**Current state, and this is a hard blocker:** `ShopInventory/Middleware/IdempotencyMiddleware.cs:151-213`
fires on the presence of the header for **any** path, not only the paths it lists. `/api/vansales/*` is not
in `HandlerOwnedIdempotencyEndpoints` (`:70-85`), so if the handset started sending the header today the
middleware would reserve the key in a per-process dictionary and answer a repeat with:

- `409 Conflict` and `{"message":"An identical request is already in progress."}` while the first is in
  flight, or
- the remembered status code and `{"message":"Duplicate request detected. …"}` afterwards.

Either way the per-sale `results` array is lost, and the handset cannot tell which sales were taken. That is
strictly worse than sending no header at all.

**Sequencing.** The ShopInventory side must first add `POST /api/vansales/sales` and
`POST /api/vansales/order` to `HandlerOwnedIdempotencyEndpoints`, so the middleware steps aside and the
handler — which already owns the real, persistent, cross-instance guard — answers. Only then should the
handset start sending the header. Until it lands, **send `van_order` in the body only**; that is the
contract that works today.

No `Idempotency-Key` header is set anywhere in the handset today (grep for `Idempotency` over
`KefalosVanSales/Pipeline` and `KefalosVanSales/Services` returns nothing).

---

## 5. The lease

`GET /api/vansales/fiscal/lease` (`VanSalesCompatibilityController.cs:366-383`). Returned bare — not wrapped
in the legacy `{"success": …}` envelope the neighbouring routes use.

Collected while online, spent while offline. It is a *lease*, not a config, because the part that matters —
the receipt sequence — is borrowed on the understanding that this handset is the sole writer of the chain
(`ShopInventory/DTOs/VanSalesFiscalLeaseDto.cs:5-13`).

### 5.1 Fields

| JSON field | Type | Meaning |
| --- | --- | --- |
| `device_id` | int | The ZIMRA device this handset signs as. Component 1 of the canonical payload. Never shared with another handset. |
| `device_serial_no` | string | Printed on the receipt. |
| `qr_url` | string | Per device. First segment of every QR payload. A blank one blocks offline trading — a receipt printed now could not be verified. |
| `fiscal_day_no` | int | The open day. Sent back on upload as `fiscal_day_no`. |
| `fiscal_day_opened_at` | string, nullable | `yyyy-MM-ddTHH:mm:ss`, **local wall clock, no offset** — deliberately, so the handset's comparison against its own clock does not depend on where the server runs (`VanSalesFiscalLeaseDto.cs:29-34`). Sent back on upload as `fiscal_day_opened_at`. |
| `fiscal_day_open` | bool | True only when FDMS reports the day as `FiscalDayOpened` (`GetVanSalesFiscalLeaseHandler.cs:38, 143-146`). |
| `taxpayer_day_max_hrs` | int | How long ZIMRA lets this taxpayer's fiscal day stay open. |
| `certificate_valid_till` | string, nullable | Same local format. |
| `next_global_no` | int | The global number the **next** receipt takes. Never resets. The lease hands over the next free position, not the last used one (`GetVanSalesFiscalLeaseHandler.cs:150-154`). |
| `next_counter` | int | The counter the **next** receipt takes. Restarts at 1 each fiscal day. |
| `taxes[]` | `{tax_id:int, percent:decimal?, code:string?}` | The rates in force. `percent` null = untaxed, `0` = zero-rated; they sign differently and must stay distinct. |
| `item_taxes[]` | `{item_code:string, tax_id:int, hs_code:string?}` | Which tax each item attracts, and its HS code. |

**Absence is meaningful.** An item whose SAP VAT group is blank, or whose group has no FDMS tax id for this
taxpayer, is simply **left out** of `item_taxes` rather than defaulted
(`GetVanSalesFiscalLeaseHandler.cs:22-27`). The handset must then refuse to sell that item offline, by name.
`OfflineSalePolicy` already does this and names the unmapped, untaxed and HS-code-less items separately
(`KefalosVanSales/Services/Fiscal/OfflineSalePolicy.cs:85-128`).

### 5.2 Send `?pendingSales=N`

The endpoint accepts an optional `pendingSales` query parameter
(`VanSalesCompatibilityController.cs:369-371`). **The handset does not send it today** —
`OfflineSaleCaptureService.RefreshLeaseAsync` calls `HttpService.Get(RouteList.VanSalesFiscalLease)` with no
query string (`KefalosVanSales/Services/OfflineSaleCaptureService.cs:347`, `Pipeline/RouteList.cs:35`).

Why it matters: a lease request is the one call every handset makes whenever it has signal, and it is the
**only** way the office learns how many signed receipts a handset is still carrying
(`GetVanSalesFiscalLeaseQuery.cs:8-15`). The server records it on every lease issue
(`GetVanSalesFiscalLeaseHandler.cs:103, 186-193`), and the office uses it to decide whether offline signing
can be moved to another van without stranding receipts.

A handset that never reports is recorded as **unknown**, and unknown blocks a handover exactly as a non-zero
count does — deliberately, so an old handset saying nothing cannot make a handover *look* safe
(`ShopInventory/Models/Entities/FiscalDeviceOfflineLeaseEntity.cs:85-93`). The office can override with a
Force flag, but that discards the safety check
(`Features/FiscalisationConfiguration/Commands/AssignOfflineSigningLease/AssignOfflineSigningLeaseHandler.cs:82-85, 128-145`).

**So: every lease request must carry `?pendingSales=N`, where N is the handset's current pending upload
queue depth** — `GetPendingCountAsync()` from `IOfflineSalesUpload`. Send `0` when the queue is empty;
`pendingSales=0` is what clears the release stamp and makes a handover safe
(`GetVanSalesFiscalLeaseHandler.cs:192`). Do not omit the parameter to mean zero — null and zero are
recorded differently and mean different things.

### 5.3 Refuse to trade offline when the lease says no

`OfflineSalePolicy.Evaluate` (`KefalosVanSales/Services/Fiscal/OfflineSalePolicy.cs:24-131`) already
implements the gate. Keep every one of these, and re-evaluate them **against the handset's own clock before
each sale**, not once at lease collection — the lease cannot carry certainty about the future:

| Condition | Refusal |
| --- | --- |
| No lease at all | "Connect to the network to collect one before trading offline." |
| `device_id` ≤ 0 | Not registered as a fiscal device. |
| `qr_url` blank | A receipt printed now could not be verified. |
| **`fiscal_day_open` false** | "No fiscal day is open for this handset. A day can only be opened online." |
| `fiscal_day_opened_at` unset | The lease does not say when its day was opened. |
| **`now − fiscal_day_opened_at ≥ taxpayer_day_max_hrs`** | The day has outrun the taxpayer's limit and must be closed online before trading continues. Closing is a network operation. |
| `certificate_valid_till` in the past | The certificate has expired; renew online. |
| `next_global_no < 1` or `next_counter < 1` | No sequence, so the next receipt could not be numbered. |
| Any item unmapped / untaxed / without an HS code | Named individually. FDMS refuses a VAT-payer line with no HS code, and says so only at day upload. |

The bias is absolute: anything unproven means no offline sale. Refusing costs one sale; selling on a broken
lease can cost the day (`OfflineSalePolicy.cs:11-19`).

### 5.4 Merging a refreshed lease

`FiscalLeaseMerge.Merge` (`KefalosVanSales/Services/Fiscal/FiscalLeaseMerge.cs:23-72`) is already correct and
must not be simplified. The rules:

- A different `device_id` from the one this handset already holds is **rejected outright** and the existing
  lease is left untouched. It is a misconfiguration, not a transient failure (`:40-46`).
- A refresh **never moves the sequence backwards**. The server's view is stale by definition while the van
  has an unuploaded backlog; adopting it would hand out numbers already signed and printed (`:9-16`).
- A **later** `fiscal_day_no` from the server is the one moment the counter legitimately returns to 1 —
  accept the reset, but still take `max` of the global numbers (`:48-54`).
- An **earlier** `fiscal_day_no` means the server is behind: keep the local day, sequence and chain hash,
  and adopt only the config the server is always authoritative for — QR URL, certificate expiry, tax table,
  item map (`:56-60, 74-100`).
- **Same day:** take whichever position is further ahead, and keep the local chain hash unless this handset
  has signed nothing in the day yet (`:63-71`).

### 5.5 Chaining

The chain starts empty on the first receipt of a fiscal day and every later receipt is signed onto its
predecessor's hash: `previousHash = counter <= 1 ? null : lease.PreviousReceiptHash`
(`KefalosVanSales/Services/OfflineSaleCaptureService.cs:247-249`). After a successful commit, the lease's
`PreviousReceiptHash` advances to the hash just produced, in the **same transaction** that stores the sale,
takes the goods off the van and advances the sequence (`:262-266`). Print only after that commit returns —
printing first lets a crash hand the customer a receipt the handset has no record of, with a number it will
hand out again (`:82-88`).

---

## 6. Signed fields on the online path too

Every sale from a handset-owned device must be handset-stamped, whatever the network was doing. A van with
signal that lets the server fiscalise is producing a receipt the platform signed with a *different* device's
key — a second writer on a chain that must have exactly one. See §1.

**The server side of this is now in place.** `VanSalesOrderRequest` and `VanSalesOrderItemRequest` carry the
same signed fields as their offline counterparts, under the same JSON names, and both implement
`IVanSalesSignedReceipt` so the completeness rules cannot drift apart
(`ShopInventory/Common/Sales/VanSalesSignedReceipt.cs`). `POST /api/vansales/order` sends
`Fiscalize = false` whenever the handset stamped, and on a successful post it stores the receipt as a
`DesktopSaleEntity` under `SaleSourceSystems.VanSalesOnline` with `ReceiptIngestStatus = Pending`, which the
same drain then hands to the platform. What is left is on the handset: it does not yet send these fields on
this path — item 7 in the checklist below.

**The contract: both paths carry the identical set of signed fields.** Given the same cart, the canonical
payload, the hash and the signature are byte-identical whichever endpoint carries them. There is one
signing routine on the handset and it does not know which endpoint the result is bound for.

The one thing the server does differently between the two is what it does with the *sale*, not the receipt.
An online sale is already in SAP and already counted as its confirmed `StockReservation`, so its row is
written `Consolidated`, with the posted document on it, under a source no posting route and no revenue
report reads — otherwise every online sale would be counted twice and invoiced twice
(`ShopInventory/Common/Sales/SaleSourceSystems.cs`, `VanSalesOnline`).

### 6.1 Receipt-level fields

| JSON field | Type | Rule |
| --- | --- | --- |
| `van_order` | string | Idempotency key and the receipt's invoice number. §4. |
| `currency` | string | Trimmed, upper-cased. Component 3 of the payload. |
| `amount_paid` | decimal | Not what the receipt is paid: the platform anchors the receipt's payment to its own derived total, a fiscal invoice being paid in full (`ReceiptBuilder.cs:118-121`). This is the cash figure, and the two DTOs mean different things by it — see below. |
| `payment_method` | string | The **brand** — "Cash", "Ecocash", "Innbucks" — not the ZIMRA money type. Both wallets are `MobileWallet` on the receipt, and telling them apart is what the cash reconciliation needs (`OfflineSaleCaptureService.cs:308-313`). |
| `fiscal_device_id` | string | The lease's `device_id`, as text. |
| `fiscal_day_no` | int | The lease's `fiscal_day_no`. |
| `receipt_global_no` | int | Signed. Required — rejected if absent or ≤ 0 (`IngestVanSalesOfflineSalesHandler.cs:556-562`). |
| `receipt_counter` | int | Carried and verified, **not** in the signed payload. |
| `receipt_date` | datetime | `yyyy-MM-ddTHH:mm:ss` local, second precision. Signed. Distinct from `sold_at` even when they are the same instant: `sold_at` decides the trading day and may be normalised; a one-second difference here invalidates the signature (`VanSalesOfflineSaleRequest.cs:96-104`). |
| `fiscal_day_opened_at` | datetime | Same format. The platform never opened this day, so it has no other way to learn it (`:106-111`). |
| `previous_receipt_hash` | string, nullable | Base64 text, or null for the first receipt of a day. Sent explicitly rather than inferred, so a divergence is reported as the chain break it is (`:113-119`). |
| `device_signature_hash` | string | Base64 SHA-256 of the canonical payload. |
| `device_signature_value` | string | Base64 RSA-PKCS1-SHA256 over the same payload. |
| `verification_code` | string | 16 hex chars, upper case — first 16 of the MD5 of the **decoded signature bytes** (`FiscalQrPayloadBuilder.cs:52-73`). |
| `qr_code` | string | `qrUrl.TrimEnd('/') + "/" + deviceId:D10 + receiptDate:ddMMyyyy + globalNo:D10 + verificationCode` (`:41-47`). |

The table above is the set **both** DTOs carry, under the same JSON names
(`ShopInventory/DTOs/VanSalesOrderRequest.cs`, `VanSalesOfflineSaleRequest.cs`). Two more fields exist on
the offline batch only and must not be sent on the online path, where the DTO has no property to bind them
to and they would be silently dropped:

| JSON field | Type | Rule | Where |
| --- | --- | --- | --- |
| `total` | decimal | Must equal the derived `receiptTotal` exactly (§3.1 step 3). | offline only |
| `vat_amount` | decimal | Σ of the tax block's `taxAmount`. Reporting only; not signed directly. | offline only |

Neither is a loss on the online path. The server derives both from the lines regardless — `total` as
Σ round(price × qty) and `vat_amount` by the tax-inclusive extraction — because the receipt is signed over
the lines and not over a total, so a total sent alongside them is a second opinion about a number with
exactly one right answer
(`Features/VanSalesCompatibility/Commands/CreateVanSalesDirectInvoice/CreateVanSalesDirectInvoiceHandler.cs`,
`BuildReceiptRow`). The platform re-derives them a third time and takes nobody's word for either (§6.3).

Non-fiscal fields differ freely between the two — the offline batch also carries `customer_name`,
`sold_at` and `payment_reference`; the online path carries `customer`, `ref`, `type`, `due_date`,
`change`, `auto_post`, `latitude`, `longitude` and `sales_order` / `sales_order_id`. `customer_code` is on
both.

**One of them changes what `amount_paid` means, and it is easy to miss.** The online DTO has a `change`
field beside it, so its `amount_paid` is the **tender** — what the customer handed over, 100.00 against a
92.50 sale. The offline DTO has no `change` field and its `amount_paid` is the settled amount. The server
stores the settled figure on both paths, subtracting `change` on the online one, so the cash column means
the same thing whichever route a sale took. Send `change` whenever the customer was given any, or the
reconciliation reads the tender as takings.

### 6.2 Line fields

| JSON field | Type | Rule |
| --- | --- | --- |
| `code` | string | SAP item code. Required and non-blank (`IngestVanSalesOfflineSalesHandler.cs:529-532`). Not part of the ZIMRA receipt — a receipt line has no item-code field — but SAP needs it to invoice. |
| `description` | string | **Becomes the receipt line name.** Required, but not for signature reasons — the line name is *not* a component of the canonical payload (§3.2), so omitting it does not break the hash. It breaks two other things instead: local preflight blocks a line with no name before the receipt is offered (`ShopInventory/Services/Fiscalisation/ReceiptPreflight.cs:283-285`), and if it got past that the platform dereferences it unguarded — `l.Name!.Trim()` (`ReceiptBuilder.cs:30`) — so a null is a server-side null-ref, not a verification failure. |
| `quantity` | decimal | Must be > 0 (`IngestVanSalesOfflineSalesHandler.cs:534-537`). |
| `price` | decimal | The **tax-inclusive unit price the receipt was signed over**, already rounded to 2dp. Not a list price. The platform recomputes the line total and the whole tax block from it. |
| `tax_code` | string, nullable | Send trimmed and upper-cased — see §3.1 step 2. |
| `tax_id` | int, nullable | From the lease's `item_taxes`. |
| `tax_percent` | decimal, nullable | From the lease's `taxes`. **Null and zero are different.** |
| `hs_code` | string, nullable | 4 or 8 digits, from the lease. FDMS refuses a VAT-payer line without one and says so only at day upload. |
| `uom_code` | string, nullable | The unit the line was sold and priced in — the unit `quantity` counts and `price` is per. Nothing fiscal depends on it; the van sales reports do. `VanSaleLineFact` cannot total a quantity without it, because summing across items adds eaches to kilograms and yields a figure that looks like a number and is not one. The product endpoints already return it as `UoM`; send back what they sent. |
| `discount_percent` | decimal, nullable | The discount given on the line, as a percentage. **Reported, never applied** — `price` above is the signed, tax-inclusive figure and is already net of it. The server stores the percentage and leaves the money alone; recomputing a line total from the two would restate a figure ZIMRA holds a signature over. Absent means no discount, not a zero-price line. |

Both of the last two are nullable throughout and absent from handsets that predate them, which reads as
*not recorded* rather than as any particular unit or a full discount
(`IngestVanSalesOfflineSalesHandler.cs`, `VanSalesCompatibilityMapper.MapServerAllocatedInvoiceLine`).

Line order is part of the contract: the server rebuilds `ReceiptLineNo` from the array index
(`ReceiptBuilder.cs:24-27`) and the API stores `LineNum` from it
(`IngestVanSalesOfflineSalesHandler.cs:575-577`), so the receipt is rebuilt in the order the lines arrived.

`HasSignedReceipt()` is the server's completeness test and is worth mirroring locally before enqueueing:
`device_signature_hash` and `device_signature_value` both non-blank, `receipt_date` present and non-default,
and `fiscal_day_no`, `receipt_counter`, `receipt_global_no` all > 0
(`ShopInventory/DTOs/VanSalesOfflineSaleRequest.cs:129-137`).

### 6.3 What the server does with them

The API stores the fields verbatim and queues the sale for `VanSalesSignedReceiptIngestService`, which
builds `IngestSignedReceiptApiRequest` and posts it to `POST /api/receipts/ingest-signed` on the platform
(`ShopInventory/Services/VanSalesSignedReceiptIngestService.cs:446-550` builds it, `:212` posts it).
Nothing is defaulted or re-derived on the way — every value was covered by the signature and a substitute
is refused as tampering, correctly (`:446-450`).

The platform then rebuilds the receipt with the **same** `BuildReceiptFromApiRequest` the server-signed path
uses, stamps on the client's sequence and signature, and verifies
(`Fiscalisation/src/Fiscalisation.Web/Endpoints/FiscalIntegrationEndpoints.IngestSigned.cs:71-86`). It takes
the client's word for the fiscal day, the sequence and the chain — and verifies those against its archive.
It does **not** take the client's word for the tax breakdown or the total (`:16-18`).

Rejections a client can cause, from `IngestSigned.cs:26-69`: no lines; more than **500** lines
(`Fiscalisation/src/Fiscalisation.Domain/Models/ReceiptInputLimits.cs:6`); `deviceId` ≤ 0; missing or
default `receiptDate`; either signature field blank. Split or refuse an oversized cart before signing —
past 500 lines the receipt is unsendable and the number is already spent.

A rejection classified as a **chain break** stops that device permanently until a person reconciles it —
retrying cannot fix it and must not be attempted
(`ShopInventory/Models/Entities/DesktopSaleEntity.cs:48-52`).

---

## 7. Rollout

The fleet updates over weeks and a handset on an old build cannot sign. Refusing its sales would stop the
van trading rather than make it compliant.

The server writes one of three outcomes per sale, and the two that lack a usable receipt have opposite
consequences for the device (`IngestVanSalesOfflineSalesHandler.cs:224-225, 650-659`):

| Sale | Recorded as | Effect on the device |
| --- | --- | --- |
| Signed | `ReceiptIngestStatus = Pending` (1), `FiscalizationStatus = Success` | Queued for the platform. |
| Carries a receipt number but no usable signature | `Unsignable` (5), `FiscalizationStatus = Success` | **Stops the device.** The number is spent, so it is a hole in the chain and everything signed after it waits behind it (`VanSalesSignedReceiptIngestService.cs:404-407`). |
| Claims no receipt sequence at all | `Unstamped` (6), `FiscalizationStatus = Failed` with the reason written onto the row (`:625-631`) | Nothing. It took no number, so it holds no position in the chain and the drain skips it deliberately (`VanSalesSignedReceiptIngestService.cs:64-78`, `DesktopSaleEntity.cs:60-76`). See the reachability note below. |

**Phase 1 — accept and flag.** `FiscalisationSettings.RequireStampedVanSales` is **off** by default
(`ShopInventory/Configuration/FiscalisationSettings.cs:112-124`, `ShopInventory/appsettings.json:209`). The
sale is accepted, held and posted — the money is real — and recorded so the tail of un-updated handsets is
visible and shrinking. A numbered-but-unsigned sale is logged at **Error** with the reference, receipt
numbers, device and fiscal day; a never-stamped one at **Warning** with the reference and device, because
nothing is blocked behind it (`:260-288`). Either way the API writes an audit row per sale (`:383-393`),
counts it on the batch audit entry (`:469-474`, `:507-510`), and tells the handset which case it is in the
per-sale message (`:290-301`):

- numbered but unsigned — *"Held for end-of-day posting, but it carries no device signature so its receipt
  cannot be submitted to ZIMRA."*
- never stamped — *"Held for end-of-day posting. This handset stamped no fiscal receipt, so the sale has no
  ZIMRA record — update the handset."*

**Phase 2 — refuse.** Turn `RequireStampedVanSales` on once that count reaches zero. After that an
unstamped sale is a bug, not a straggler. **The switch is live** — with it on, a sale claiming no receipt
sequence is refused outright, with *"This sale carries no fiscal receipt. Stamped receipts are now
required, so it cannot be accepted — update the handset to a build that signs receipts."*
(`:230-251`). It refuses only that case: a sale that took a number and lost its signature is still accepted
and still stops its device, because the number is spent either way and refusing it would lose the takings
without healing the chain.

Note for planning: the never-stamped row above **cannot be reached through this endpoint today.**
`Validate` runs before either branch and refuses any sale whose `receipt_global_no` is absent or ≤ 0, with
*"receipt_global_no is required — a van sale reaches this endpoint already fiscalised."* (`:197`,
`:556-562`), and that refusal is pinned by a test
(`ShopInventory.Tests/VanSalesOfflineIngestTests.cs:199-210`). So a handset too old to number its receipts
at all is answered `rejected` and keeps the sale, rather than getting the accept-and-flag treatment phase 1
describes; `Unstamped` is written nowhere at runtime while that validation stands. Whether the validation
relaxes for the unstamped case, or phase 1 only ever covers handsets that number their receipts, is a
ShopInventory-side decision. Stated here so the handset team does not plan around an acceptance that does
not happen yet.

Also landed on the server: one handset per fiscal device is now enforced by a unique filtered index on
`Users.FiscalDeviceId`, not only by the application's read-then-write check
(`ShopInventory/Data/ApplicationDbContext.cs:391-400`; migration
`ShopInventory/Migrations/20260818113735_AddFiscalisationPreflightAndDayLifecycle.cs:105-110`, which
refuses to apply while any device id has more than one handset and names them, `:57-76`). That closes the
race on the *nomination* in §1; it still cannot enforce that only one physical handset holds that user's
credentials.

### Version skew, in both directions

| Skew | Symptom | Handling |
| --- | --- | --- |
| Old handset → current server | No `pendingSales` on the lease call | Recorded as **unknown**, which blocks a device handover until forced (`FiscalDeviceOfflineLeaseEntity.cs:85-93`). Not an error. |
| Old handset → current server | Receipt numbers on the upload but no signature | Accepted, flagged, counted — and the device stops until a person reconciles it (phase 1 above). |
| Old handset → current server | No fiscal fields on the upload at all | **Rejected**, and the sale stays on the handset: validation requires `receipt_global_no` (`IngestVanSalesOfflineSalesHandler.cs:556-562`). Not the accept-and-flag path — see the reachability note in §7. |
| Old handset → current server | No `payment_method` | Left null rather than assumed to be cash — an assumed tender in a cash-control report is worse than an absent one (`VanSalesCompatibilityMapper.cs:142-146`). |
| Current handset → old platform | Platform has no `/api/receipts/ingest-signed` route | The API detects the 404, marks **nothing** failed, and logs that the platform build predates the service (`ShopInventory/Services/VanSalesSignedReceiptIngestService.cs:237-259`). Receipts wait rather than being lost. |
| Current handset → old API | `pendingSales` query param unrecognised | Harmless — an unbound query parameter is ignored by ASP.NET model binding. |

There is no version negotiation. Adding a field is safe in both directions; changing the meaning of one is
not.

---

## Implementation checklist

| # | Change | Where |
| --- | --- | --- |
| 1 | Provisioning screen: `EnsureCreated()`, CSR PEM display + copy | new, KefalosVanSales |
| 2 | Fail closed at app start when `AndroidDeviceSigningKey.Exists` is false | `MauiProgram.cs` / `OfflineSalePolicy.cs` |
| 3 | Group taxes by (taxId, taxPercent, taxCode), not taxId alone | `Services/Fiscal/FiscalReceiptComposer.cs:127-148` |
| 4 | Send `tax_code` already trimmed and upper-cased | `Services/Fiscal/OfflineSaleWireMapper.cs:47` |
| 5 | **Already done** — run the §3.4 golden vectors and confirm they still pass; keep them identical to the platform's copy | `KefalosVanSales.Tests/Fiscal/ReceiptCanonicalPayloadTests.cs` |
| 6 | Send `?pendingSales=N` on every lease refresh | `Services/OfflineSaleCaptureService.cs:347` |
| 7 | Stamp the online direct-invoice path with the same signed fields | `Views/SalesCartSummary.xaml.cs` — no longer blocked: the ShopInventory DTOs, the store and the drain all take them (§6). Send `description` on every line: it is the receipt line's name. Not a signature component — a missing one is blocked by preflight, and past that is a null-ref on the platform (§6.2) |
| 8 | Send `van_order` as an `Idempotency-Key` header | blocked on §4.4; body-only until then |

---

## Open questions

These are genuinely unresolved in the source read for this document. They are listed rather than guessed.

1. **The CSR subject DN per device.** `AndroidDeviceSigningKey.cs:108-110` gives
   `CN=ZIMRA-VAN006-0000035410` as an example. Whether the numeric part is the device id, the device serial,
   or a ZIMRA-issued value, and whether ZIMRA validates the DN at registration, is not established anywhere
   in these three repos. Operations input needed before the provisioning screen can compose it.

2. **Where the handset gets its `deviceId` and activation key before it has a lease.** The lease reports
   `device_id`, but a lease cannot be issued until the user has `FiscalDeviceId` set
   (`GetVanSalesFiscalLeaseHandler.cs:65-73`), which is set only after registration. The provisioning screen
   therefore needs the device id from somewhere else — manual entry, or a new endpoint. Not resolved.

3. **Credit notes offline.** The van path assumes `FISCALINVOICE` throughout, and the API forwarder
   hard-codes `ReceiptType.FiscalInvoice` on the grounds that *"a van sells; it never issues a credit note
   out of coverage"* (`VanSalesSignedReceiptIngestService.cs:505-507`). The canonical payload supports
   `CREDITNOTE` and `DEBITNOTE`, and `SubmitReceiptApiRequest` carries a `CreditDebitNote` block
   (`ReceiptBuilder.cs:104-114`), but no offline credit-note flow exists. Whether one is wanted is a product
   question.

4. **Multi-currency within one fiscal day.** The lease carries a single tax table with no currency
   dimension, the canonical payload carries one currency per receipt, and the day-close counters are
   per-currency (`ReceiptCanonicalPayload.cs:86-92`). Whether a van may trade USD and ZiG in the same fiscal
   day on one device — and if so what the handset does when the currency changes mid-day — is not answered
   by any of the three repos.

5. **Buyer details.** The forwarder sends `Buyer = null` deliberately, because *"the handset signed a
   receipt without one, and adding buyer details here would archive a document that differs from the one
   that was printed"* (`VanSalesSignedReceiptIngestService.cs:521-523`). Whether a VAT-registered buyer ever
   needs their TIN on a van receipt — which would change what the handset must capture and print — is
   unresolved.
