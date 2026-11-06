"""Stub ShopInventory API for driving /customer-portal/credit-notes on the real Web.

Answers the two routes the page calls, logs every request at /__requests, and 404s the rest.
HTTP/1.1 + ThreadingHTTPServer: the Web's HttpClient keeps one pooled connection alive.
"""
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PORT = int(sys.argv[1])
PDF_PATH = sys.argv[2]
CUSTOMER = "C20417"
LOG = []
LOCK = threading.Lock()


def note(doc_num, day, invoice, reason, total, vat, applied, receipt=0, lines=()):
    return {
        "docEntry": doc_num + 31000, "docNum": doc_num, "docDate": f"2026-{day}T00:00:00Z",
        "cardCode": CUSTOMER, "cardName": "Greenfield Supermarket", "currency": "USD",
        "docTotal": total, "vatTotal": vat, "appliedAmount": applied, "creditLeft": round(total - applied, 2),
        "creditedInvoiceDocNum": invoice, "reason": reason,
        "isFiscalised": receipt > 0, "fiscalReceiptNumber": receipt,
        "fiscalVerificationCode": "AB12-CD34-EF56-7890" if receipt else None,
        "fiscalVerificationUrl": f"https://fdms.zimra.co.zw/verify/{receipt}" if receipt else None,
        "lines": [
            {"itemCode": code, "description": name, "quantity": qty, "uoMCode": "EA",
             "unitPrice": price, "lineTotal": round(qty * price, 2), "reason": reason}
            for code, name, qty, price in lines
        ],
    }


NOTES = [
    note(30418, "11-04", 10512, "Short-dated stock", 186.40, 24.31, 0, 48213,
         [("YOG-500", "Natural yoghurt 500 ml", 24, 1.85), ("CHD-1000", "Mature cheddar 1 kg", 8, 12.50),
          ("MLK-2000", "Fresh milk 2 L", 20, 2.10)]),
    note(30402, "10-30", 10488, "Damaged in transit", 226.10, 29.49, 0, 0,
         [("BUT-500", "Salted butter 500 g", 26, 4.85), ("CRM-250", "Fresh cream 250 ml", 40, 2.50)]),
    note(30391, "10-27", 10455, "Promotion price not applied", 94.75, 12.36, 94.75, 47988,
         [("GOU-1000", "Gouda 1 kg, price difference", 25, 3.79)]),
    note(30377, "10-22", 10431, "Wrong item delivered", 312.00, 40.70, 150.00, 47902,
         [("MOZ-2000", "Mozzarella 2 kg", 12, 18.50), ("FET-400", "Feta 400 g", 18, 5.00)]),
    note(30360, "10-17", 0, None, 140.00, 18.26, 140.00, 47811,
         [("DISCOUNT", "Volume discount, September", 1, 140.00)]),
    note(30344, "10-13", 10388, "Leaking cartons", 58.30, 7.60, 58.30, 47760,
         [("MLK-1000", "Fresh milk 1 L", 22, 1.15), ("DRY-500", "Drinking yoghurt 500 ml", 30, 1.10)]),
    note(30329, "10-08", 10361, "Duplicate order", 1042.00, 135.91, 1042.00, 47655,
         [("CHD-1000", "Mature cheddar 1 kg", 40, 12.50), ("YOG-500", "Natural yoghurt 500 ml", 120, 1.85),
          ("MLK-2000", "Fresh milk 2 L", 100, 2.10), ("FET-400", "Feta 400 g", 22, 5.00)]),
    note(30311, "10-02", 10340, "Over-delivered", 77.60, 10.12, 77.60, 47590,
         [("YOG-500", "Natural yoghurt 500 ml", 16, 1.85), ("GOU-1000", "Gouda 1 kg", 4, 12.00)]),
]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _send(self, status, body, content_type="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _drain(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            while True:
                size = int(self.rfile.readline().strip() or b"0", 16)
                self.rfile.read(size + 2)
                if size == 0:
                    break
        elif self.headers.get("Content-Length"):
            self.rfile.read(int(self.headers["Content-Length"]))

    def do_POST(self):
        self._drain()
        with LOCK:
            LOG.append({"method": "POST", "path": self.path})
        self._send(404, {"title": "stub: not answered"})

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/__requests":
            with LOCK:
                return self._send(200, LOG)
        with LOCK:
            LOG.append({"method": "GET", "path": self.path})

        query = parse_qs(url.query)
        if url.path.lower() == "/api/creditnote/customers":
            codes = {code.upper() for code in query.get("cardCodes", [])}
            notes = [n for n in NOTES if n["cardCode"] in codes]
            return self._send(200, {"creditNotes": notes})

        parts = url.path.strip("/").split("/")
        if len(parts) == 4 and parts[0].lower() == "api" and parts[1].lower() == "creditnote" and parts[3] == "pdf":
            doc_entry = int(parts[2])
            if query.get("cardCode", [""])[0].strip().upper() != CUSTOMER or doc_entry not in {n["docEntry"] for n in NOTES}:
                return self._send(404, {"title": "CreditNote.NotFound"})
            with open(PDF_PATH, "rb") as f:
                return self._send(200, f.read(), "application/pdf")

        self._send(404, {"title": "stub: not answered"})


ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
