#!/usr/bin/env node
/*
 * Reads received WhatsApp messages back out of an OpenWA session's own WhatsApp Web profile and
 * writes each one as the exact `message.received` webhook body OpenWA would have posted.
 *
 * Why this exists: until ShopInventory#762 the API stored only the first inbound message ever
 * received (OpenWA keyed every message `msg_unknown`). OpenWA keeps no copy of what it receives -
 * its `messages` table holds outgoing sends only, saveIncomingMessage has no caller, and the webhook
 * queue is off on .9 - so the only record left is WhatsApp's own chat history on the linked device.
 *
 * It changes nothing in ShopInventory. Import-WhatsAppBackfill.ps1 reads its output.
 *
 * RUN IT WITH THE GATEWAY STOPPED. The profile is a Chrome user-data directory, and one Chrome
 * holds it at a time: with the gateway up the launch fails "The browser is already running".
 * Stop it with Stop-OpenWA.ps1 (never the console's Stop: that clears the session's autoStart and
 * nothing would bring it back), run this, then start the gateway again. Expect 1-3 minutes down.
 *
 * It never pairs. If the profile is not logged in, WhatsApp asks for a QR code and this exits at
 * once rather than wait for one. It closes the browser with destroy(), never logout().
 *
 * One thing it cannot prevent: if WhatsApp has already unlinked this device, WhatsApp Web redirects
 * to ?post_logout=1 and whatsapp-web.js itself deletes the profile folder (LocalAuth.logout). The
 * gateway does the same on its next start, so the session needs pairing again either way; this
 * reports "logged out" and stops. Seen on a local unpaired profile, 2026-10-16.
 *
 * Usage (on .9, from any folder; whatsapp-web.js is loaded from OpenWA's own node_modules so the
 * same version that runs the gateway reads the profile):
 *
 *   node Export-WhatsAppReceivedMessages.js --openwa-root C:\ShopInventory\OpenWA ^
 *        --since 2026-09-01 --out C:\Temp\wa-backfill
 *
 * Options:
 *   --openwa-root <dir>   OpenWA checkout. Default C:\ShopInventory\OpenWA.
 *   --session <name>      A session to read, by name; repeatable. Default: every session-<name>
 *                         folder under data\sessions. OpenWA names the profile after the session's
 *                         name, but posts its UUID as sessionId (the dedupe key is built on that),
 *                         so each name is looked up in OpenWA's own sessions table, read-only.
 *   --session-id <name>=<uuid>  The UUID for a name the sessions table no longer has (a deleted
 *                         session whose profile is still on disk). Repeatable.
 *   --since <date>        Oldest message to keep (UTC, yyyy-MM-dd). Required.
 *   --out <dir>           Output folder, created if missing; refuses a non-empty one.
 *   --per-chat <n>        Messages to load per chat, newest first. Default 1000.
 *   --no-media            Do not download media. OpenWA downloads it into data.media, so the default
 *                         mirrors what it would have posted.
 *   --ready-timeout <s>   How long to wait for WhatsApp Web to be ready. Default 180.
 *
 * Output, in --out:
 *   payloads\<n>.json     One webhook body per message, UTF-8 without BOM, posted verbatim.
 *   index.jsonl           One line per payload: file, deliveryId, sessionId, id, chatId, from, timestamp, type,
 *                         fromMe, isStatus, hasMedia, mediaSaved, bytes, preview (first 60 chars).
 *   summary.json          Per session and chat: messages loaded, kept, skipped and why, and whether
 *                         the per-chat limit was reached before --since (then raise --per-chat).
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { createRequire } = require('module');
const { randomUUID } = require('crypto');
const net = require('net');

// whatsapp-web.js 'message' never fires for these: they are system rows in the chat, not messages.
const SYSTEM_TYPES = new Set([
  'e2e_notification',
  'notification',
  'notification_template',
  'gp2',
  'protocol',
  'ciphertext',
  'call_log',
  'broadcast_notification',
]);

function parseArgs(argv) {
  const options = {
    openWaRoot: 'C:\\ShopInventory\\OpenWA',
    sessions: [],
    sessionIds: {},
    since: null,
    out: null,
    perChat: 1000,
    media: true,
    readyTimeoutSeconds: 180,
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${arg} needs a value`);
      return argv[++i];
    };
    switch (arg) {
      case '--openwa-root': options.openWaRoot = next(); break;
      case '--session': options.sessions.push(next()); break;
      case '--session-id': {
        const [name, id] = next().split('=');
        if (!name || !id) throw new Error('--session-id takes <name>=<uuid>');
        options.sessionIds[name] = id;
        break;
      }
      case '--since': options.since = next(); break;
      case '--out': options.out = next(); break;
      case '--per-chat': options.perChat = Number(next()); break;
      case '--no-media': options.media = false; break;
      case '--ready-timeout': options.readyTimeoutSeconds = Number(next()); break;
      default: throw new Error(`Unknown option ${arg}`);
    }
  }
  if (!options.since || !/^\d{4}-\d{2}-\d{2}$/.test(options.since)) throw new Error('--since yyyy-MM-dd is required');
  if (!options.out) throw new Error('--out is required');
  if (!Number.isInteger(options.perChat) || options.perChat < 1) throw new Error('--per-chat must be a positive integer');
  return options;
}

/** Reads KEY=VALUE lines from OpenWA's .env, the file the gateway itself starts from. */
function readEnv(openWaRoot) {
  const envPath = path.join(openWaRoot, '.env');
  const values = {};
  if (!fs.existsSync(envPath)) return values;
  for (const line of fs.readFileSync(envPath, 'utf8').split(/\r?\n/)) {
    const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (match && !line.trimStart().startsWith('#')) values[match[1]] = match[2].replace(/^["']|["']$/g, '');
  }
  return values;
}

/**
 * The IncomingMessage OpenWA builds in whatsapp-web-js.adapter.ts ('message' handler), field for
 * field and in the same order, so the stored RawPayload looks like a live one.
 */
async function toIncomingMessage(msg, { media }) {
  const incoming = {
    id: msg.id._serialized,
    from: msg.from,
    to: msg.to,
    chatId: msg.from,
    body: msg.body,
    type: msg.type,
    timestamp: msg.timestamp,
    fromMe: msg.fromMe,
    isGroup: typeof msg.from === 'string' && msg.from.endsWith('@g.us'),
  };
  let mediaSaved = false;
  if (media && msg.hasMedia) {
    try {
      const downloaded = await msg.downloadMedia();
      if (downloaded) {
        incoming.media = {
          mimetype: downloaded.mimetype,
          filename: downloaded.filename || undefined,
          data: downloaded.data,
        };
        mediaSaved = true;
      }
    } catch {
      // OpenWA logs and carries on without it; old media has often expired on WhatsApp's servers.
    }
  }
  if (msg.hasQuotedMsg) {
    try {
      const quoted = await msg.getQuotedMessage();
      if (quoted) incoming.quotedMessage = { id: quoted.id._serialized, body: quoted.body };
    } catch {
      // As OpenWA: a quote that cannot be read is left out.
    }
  }
  return { incoming, mediaSaved };
}

/** The body WebhookService.dispatch posts. idempotencyKey is what OpenWA#7 sends for a message. */
function toWebhookPayload(sessionId, incoming) {
  return {
    event: 'message.received',
    timestamp: new Date().toISOString(),
    sessionId,
    idempotencyKey: `msg_${incoming.id}`,
    deliveryId: `backfill-${randomUUID()}`,
    data: incoming,
  };
}

/** Why a history row is not something OpenWA would have dispatched as message.received. */
function skipReason(msg, sinceEpochSeconds) {
  if (msg.fromMe) return 'fromMe';
  if (SYSTEM_TYPES.has(msg.type)) return `system:${msg.type}`;
  if (msg.type === 'revoked') return 'revoked';
  if (!msg.timestamp || msg.timestamp < sinceEpochSeconds) return 'beforeSince';
  return null;
}

/**
 * Writes a history row out if OpenWA would have dispatched it, once per message id: the history
 * sweep and the live listener can both see the same message.
 */
function makeKeeper({ sessionId, sinceEpochSeconds, options, writer, sessionSummary }) {
  const seen = new Set();
  return async (msg, chatSummary) => {
    const id = msg.id && msg.id._serialized;
    if (!id || seen.has(id)) return;
    const reason = skipReason(msg, sinceEpochSeconds);
    if (reason) {
      chatSummary.skipped[reason] = (chatSummary.skipped[reason] || 0) + 1;
      return;
    }
    seen.add(id);
    const { incoming, mediaSaved } = await toIncomingMessage(msg, options);
    writer.write(sessionId, incoming, mediaSaved, msg.isStatus === true);
    chatSummary.kept++;
    sessionSummary.kept++;
  };
}

function waitFor(client, timeoutSeconds) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`WhatsApp Web was not ready within ${timeoutSeconds}s`)), timeoutSeconds * 1000);
    client.once('ready', () => { clearTimeout(timer); resolve(); });
    client.once('qr', () => { clearTimeout(timer); reject(new Error('The profile is not logged in (WhatsApp asked for a QR code). Nothing was read; this script never pairs.')); });
    client.once('auth_failure', message => { clearTimeout(timer); reject(new Error(`Authentication failed: ${message}`)); });
    client.once('disconnected', reason => {
      clearTimeout(timer);
      reject(new Error(reason === 'LOGOUT'
        ? 'WhatsApp has unlinked this device (logged out). Nothing was read, and the session must be paired again.'
        : `Disconnected before it was ready: ${reason}`));
    });
  });
}

/** name -> UUID from OpenWA's own database, opened read-only. */
function readSessionIds(openWaRequire, openWaRoot, env) {
  if ((env.DATABASE_TYPE || 'sqlite') !== 'sqlite') {
    return Promise.reject(new Error(`OpenWA runs on ${env.DATABASE_TYPE}; pass --session-id <name>=<uuid> for each session.`));
  }
  const file = path.resolve(openWaRoot, env.DATABASE_NAME || './data/openwa.sqlite');
  const sqlite3 = openWaRequire('sqlite3');
  return new Promise((resolve, reject) => {
    const db = new sqlite3.Database(file, sqlite3.OPEN_READONLY, error => {
      if (error) return reject(new Error(`Cannot open ${file}: ${error.message}`));
      db.all('SELECT id, name FROM sessions', (queryError, rows) => {
        db.close();
        if (queryError) return reject(queryError);
        resolve(Object.fromEntries(rows.map(r => [r.name, r.id])));
      });
    });
  });
}

async function exportSession(wwjs, options, env, { name, sessionId }, writer) {
  const dataPath = path.resolve(options.openWaRoot, env.SESSION_DATA_PATH || './data/sessions');
  const profile = path.join(dataPath, `session-${name}`);
  if (!fs.existsSync(profile)) throw new Error(`No profile at ${profile}`);

  const executablePath = env.PUPPETEER_EXECUTABLE_PATH || process.env.PUPPETEER_EXECUTABLE_PATH || undefined;
  const args = (env.PUPPETEER_ARGS || '--no-sandbox,--disable-setuid-sandbox,--disable-dev-shm-usage,--disable-gpu')
    .split(',').map(a => a.trim()).filter(Boolean);

  const client = new wwjs.Client({
    authStrategy: new wwjs.LocalAuth({ clientId: name, dataPath }),
    puppeteer: { headless: true, args, ...(executablePath ? { executablePath } : {}) },
  });

  // Anything that arrives while this client holds the profile is caught here too. The gateway is
  // down meanwhile, and WhatsApp will not hand the same message to the same linked device twice.
  const live = [];
  client.on('message', msg => live.push(msg));

  const sessionSummary = { name, sessionId, profile, chats: [], live: 0, kept: 0 };
  const sinceEpochSeconds = Math.floor(Date.parse(`${options.since}T00:00:00Z`) / 1000);
  const keep = makeKeeper({ sessionId, sinceEpochSeconds, options, writer, sessionSummary });

  process.stderr.write(`[${name}] launching WhatsApp Web on ${profile} (sessionId ${sessionId})\n`);
  const ready = waitFor(client, options.readyTimeoutSeconds);
  try {
    await Promise.all([client.initialize(), ready]);
    const info = client.info && client.info.wid ? client.info.wid.user : '?';
    process.stderr.write(`[${name}] ready as ****${String(info).slice(-4)}\n`);

    const chats = await client.getChats();
    for (const chat of chats) {
      // Chat.timestamp is its newest message, so a chat quiet since before --since has nothing to give.
      if (!chat.timestamp || chat.timestamp < sinceEpochSeconds) continue;
      const chatSummary = {
        chatId: chat.id._serialized,
        isGroup: chat.isGroup,
        loaded: 0,
        kept: 0,
        skipped: {},
        oldestLoaded: null,
        limitReached: false,
      };
      let messages;
      try {
        messages = await chat.fetchMessages({ limit: options.perChat });
      } catch (error) {
        chatSummary.error = String(error && error.message ? error.message : error);
        sessionSummary.chats.push(chatSummary);
        process.stderr.write(`[${name}] ${chatSummary.chatId}: fetchMessages failed: ${chatSummary.error}\n`);
        continue;
      }
      chatSummary.loaded = messages.length;
      if (messages.length > 0) {
        const oldest = Math.min(...messages.map(m => m.timestamp || Infinity));
        chatSummary.oldestLoaded = Number.isFinite(oldest) ? new Date(oldest * 1000).toISOString() : null;
        chatSummary.limitReached = messages.length >= options.perChat && oldest >= sinceEpochSeconds;
      }
      for (const msg of messages) await keep(msg, chatSummary);
      sessionSummary.chats.push(chatSummary);
    }

    const liveSummary = { chatId: '(arrived while exporting)', loaded: live.length, kept: 0, skipped: {} };
    for (const msg of live) await keep(msg, liveSummary);
    sessionSummary.live = liveSummary.kept;
    if (live.length > 0) sessionSummary.chats.push(liveSummary);
  } finally {
    // destroy() closes the browser and keeps the login. logout() would unlink the device.
    await client.destroy().catch(() => {});
  }
  return sessionSummary;
}

function createWriter(outDir) {
  const payloadDir = path.join(outDir, 'payloads');
  fs.mkdirSync(payloadDir, { recursive: true });
  const index = fs.openSync(path.join(outDir, 'index.jsonl'), 'w');
  let n = 0;
  return {
    write(sessionId, incoming, mediaSaved, isStatus) {
      n++;
      const file = `payloads/${String(n).padStart(6, '0')}.json`;
      const payload = toWebhookPayload(sessionId, incoming);
      const body = JSON.stringify(payload);
      fs.writeFileSync(path.join(outDir, file), body, 'utf8');
      fs.writeSync(index, JSON.stringify({
        file,
        deliveryId: payload.deliveryId,
        sessionId,
        id: incoming.id,
        chatId: incoming.chatId,
        from: incoming.from,
        timestamp: incoming.timestamp,
        type: incoming.type,
        fromMe: incoming.fromMe,
        isStatus,
        hasMedia: Boolean(incoming.media) || mediaSaved,
        mediaSaved,
        bytes: Buffer.byteLength(body, 'utf8'),
        preview: typeof incoming.body === 'string' ? incoming.body.slice(0, 60) : '',
      }) + '\n');
    },
    close() { fs.closeSync(index); return n; },
  };
}

/** True when something answers on the gateway's port, i.e. OpenWA is still running. */
function portInUse(port) {
  return new Promise(resolve => {
    const socket = net.connect({ host: '127.0.0.1', port }, () => { socket.destroy(); resolve(true); });
    socket.on('error', () => resolve(false));
    socket.setTimeout(2000, () => { socket.destroy(); resolve(false); });
  });
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const env = readEnv(options.openWaRoot);

  const gatewayPort = parseInt(env.PORT || '2785', 10);
  if (await portInUse(gatewayPort)) {
    throw new Error(`OpenWA is still listening on ${gatewayPort}. Stop it with Stop-OpenWA.ps1 first: its Chrome holds the profile.`);
  }

  const outDir = path.resolve(options.out);
  if (fs.existsSync(outDir) && fs.readdirSync(outDir).length > 0) {
    throw new Error(`${outDir} is not empty. Use a new folder so two exports never mix.`);
  }

  const openWaRequire = createRequire(path.join(path.resolve(options.openWaRoot), 'package.json'));
  const wwjs = openWaRequire('whatsapp-web.js');
  const wwjsVersion = openWaRequire('whatsapp-web.js/package.json').version;

  let names = options.sessions;
  if (names.length === 0) {
    const dataPath = path.resolve(options.openWaRoot, env.SESSION_DATA_PATH || './data/sessions');
    names = fs.readdirSync(dataPath, { withFileTypes: true })
      .filter(d => d.isDirectory() && d.name.startsWith('session-'))
      .map(d => d.name.slice('session-'.length));
  }
  if (names.length === 0) throw new Error('No session profiles found.');

  const known = names.every(n => options.sessionIds[n])
    ? {}
    : await readSessionIds(openWaRequire, options.openWaRoot, env);
  const sessions = names.map(name => ({ name, sessionId: options.sessionIds[name] || known[name] }));
  const unknown = sessions.filter(s => !s.sessionId).map(s => s.name);
  if (unknown.length > 0) {
    throw new Error(`No session row for ${unknown.join(', ')}. Pass --session for the others, or ` +
      '--session-id <name>=<uuid> if you know the UUID OpenWA posted for it.');
  }

  fs.mkdirSync(outDir, { recursive: true });
  const writer = createWriter(outDir);
  const summary = {
    exportedAtUtc: new Date().toISOString(),
    since: options.since,
    perChat: options.perChat,
    media: options.media,
    whatsappWebJs: wwjsVersion,
    sessions: [],
  };

  let failed = false;
  for (const session of sessions) {
    try {
      summary.sessions.push(await exportSession(wwjs, options, env, session, writer));
    } catch (error) {
      failed = true;
      const message = String(error && error.message ? error.message : error);
      summary.sessions.push({ ...session, error: message });
      process.stderr.write(`[${session.name}] FAILED: ${message}\n`);
    }
  }
  const total = writer.close();
  fs.writeFileSync(path.join(outDir, 'summary.json'), JSON.stringify(summary, null, 2), 'utf8');

  for (const s of summary.sessions) {
    if (s.error) { console.log(`${s.name} (${s.sessionId}): FAILED - ${s.error}`); continue; }
    const hitLimit = s.chats.filter(c => c.limitReached).length;
    const errors = s.chats.filter(c => c.error).length;
    console.log(`${s.name} (${s.sessionId}): ${s.kept} received messages kept from ${s.chats.length} chats` +
      (s.live ? ` (${s.live} arrived while exporting)` : '') +
      (hitLimit ? `; ${hitLimit} chats hit --per-chat before --since, raise it` : '') +
      (errors ? `; ${errors} chats could not be read` : ''));
  }
  console.log(`${total} payloads written to ${outDir}`);
  process.exitCode = failed ? 1 : 0;
}

if (require.main === module) {
  // whatsapp-web.js rejects inside its own page listeners (the post_logout folder delete fails EBUSY
  // while Chrome holds it). Unhandled, that ends the process before the browser is closed.
  process.on('unhandledRejection', error => {
    process.stderr.write(`whatsapp-web.js: ${String(error && error.message ? error.message : error)}\n`);
  });
  main().catch(error => {
    console.error(String(error && error.stack ? error.stack : error));
    process.exitCode = 1;
  });
}

module.exports = { toIncomingMessage, toWebhookPayload, skipReason, makeKeeper, createWriter, readSessionIds, SYSTEM_TYPES };
