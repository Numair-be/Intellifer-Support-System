# Intellifer Hub

Support hub that turns customer messages from **WhatsApp** (via OpenWA) and **Gmail** into **Plane** intake tickets, using **Odoo** to recognise the client and their project, and keeps the customer informed on the channel they used.

What happens to a message:

1. It is stored in the hub's own Postgres database (deduplicated, sender normalised: phones to E.164, emails lowercased).
2. The hub waits until the conversation is quiet (WhatsApp: 2 minutes, email: no wait), then reads the whole burst of messages together.
3. If they read like a bug or error report, and the sender is an Odoo **client contact** whose **client project** matches a Plane project (by name or code), **one** intake item is opened in that Plane project with every message and every image. Otherwise the messages are skipped and the reason is recorded.
4. The customer gets a reply with the ticket number (e.g. `PLANE-12`), in the same Gmail thread or WhatsApp chat.
5. Later messages in the conversation are added to the ticket as comments. A new ticket is opened when the previous one is closed or has been idle for 24 hours.
6. When the ticket changes status in Plane (accepted, backlog, to do, in progress, done, cancelled, declined, duplicate) the customer gets an update.

The hub only reads from Odoo. In Plane it only creates intake items, comments and attachments.

## Deploy on a server

Needs a Linux server with Docker Engine and the Docker Compose plugin (`docker compose`), and outbound internet access (Plane, Odoo, Google, WhatsApp, NuGet).

1. Copy the project folder to the server, **including `.env`** (it holds the Gmail token, Odoo and Plane keys, and the WhatsApp session key). Keep `.env` private: `chmod 600 .env`.
2. Run:

   ```bash
   chmod +x deploy.sh
   ./deploy.sh
   ```

`deploy.sh`:

- checks Docker and Docker Compose,
- creates `.env` from `.env.example` if there is none,
- replaces any `dev-only-…` or too-short secrets with random 40-character ones (the WhatsApp gateway needs at least 32),
- sets a random database password on a fresh install (an existing database keeps its password),
- **checks which ports are already in use** on the server (listening sockets and other containers' published ports) and moves the hub's ports up to the next free one, writing them to `.env` (`HUB_API_PORT`, `HUB_DB_PORT`, `OPENWA_PORT`); ports held by the hub's own containers count as free,
- builds (retrying on network errors), starts the hub, and waits until the API is healthy.

Options:

| Command | Does |
| --- | --- |
| `./deploy.sh --check` | Only the checks: picks ports and fills secrets in `.env`, starts nothing. |
| `./deploy.sh` | Build and start the API, worker and database. |
| `./deploy.sh --with-whatsapp` | Also start OpenWA (see the warning under *Link WhatsApp*). |

Run `./deploy.sh` again after copying new code: it keeps the existing secrets, password and ports.

### Windows server with Docker inside WSL

Run `deploy.sh` and the `docker compose` commands **inside WSL**. The project folder `C:\IntelliferSupport` is `/mnt/c/IntelliferSupport` there. Under WSL, `deploy.sh` also checks the ports Windows programs already use, because WSL forwards the hub's ports to Windows' `localhost`.

Windows does not start WSL by itself, and WSL stops when nothing is attached to it. Pick one way to keep WSL and Docker running; Docker then restarts the hub's containers (`restart: unless-stopped`):

- **At sign-in (no admin, no password stored).** Run once, in normal PowerShell, as the user that owns the WSL distribution:

  ```powershell
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-signin-start.ps1 -StartNow
  ```

  It adds a hidden launcher to that user's Startup folder. The hub runs while that user is signed in: locking the screen is fine, signing out stops it until the next sign-in. Remove it with `-Uninstall`.

- **At boot, before anyone signs in (admin once, stores that user's password).** In PowerShell **as Administrator**:

  ```powershell
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-autostart.ps1
  ```

Use one or the other, not both.

| To | Run inside WSL, in `/mnt/c/IntelliferSupport` | After a reboot |
| --- | --- | --- |
| Turn the hub off | `docker compose --profile openwa stop` | stays off |
| Turn it back on | `docker compose --profile openwa start` | comes back by itself again |
| Update after `git pull` | `./deploy.sh --with-whatsapp` | comes back by itself |

All ports are published on `127.0.0.1` only. To reach the API from outside the server, put a reverse proxy (nginx, Caddy) with HTTPS in front of `HUB_API_PORT`, or set `HUB_API_BIND=0.0.0.0` only on a private network.

In the commands below, `API` is `http://127.0.0.1:<HUB_API_PORT>` and `OWA` is `http://127.0.0.1:<OPENWA_PORT>` (8080 and 8081 unless `deploy.sh` moved them).

## Link WhatsApp

The WhatsApp gateway is [OpenWA by rmyndharis](https://github.com/rmyndharis/OpenWA) (`ghcr.io/rmyndharis/openwa:0.29.1`, default engine, SQLite). It is unofficial; WhatsApp can rate-limit or ban the number, so use a dedicated support number.

The hub's worker creates the gateway session (`WA_SESSION_NAME`, default `support`) and its webhook to the hub by itself. The login is kept in the `openwa-data` volume, and the gateway reconnects it after any restart, so a number is linked **once**.

Link it (`$HUB_API_KEY` from `.env`):

```bash
./deploy.sh --with-whatsapp
HUB_API_KEY=$(grep '^HUB_API_KEY=' .env | cut -d= -f2-)
curl -s -X POST -H "X-Api-Key: $HUB_API_KEY" API/api/whatsapp/link
```

- With `WA_LINK_PHONE` set in `.env` (digits, country code first, e.g. `91XXXXXXXXXX`) the answer has a `pairingCode`: on the phone open **WhatsApp → Settings → Linked devices → Link a device → Link with phone number instead** and enter it.
- `…/api/whatsapp/link?phone=91XXXXXXXXXX` uses another number; `…/api/whatsapp/link?qr=true` returns a QR code instead. The QR can also be scanned from the gateway's dashboard at `http://127.0.0.1:<OPENWA_PORT>` (sign in with `WA_API_KEY`).

Check: `curl -s -H "X-Api-Key: $HUB_API_KEY" API/api/whatsapp` shows `"status":"ready"`, and `API/health/openwa` returns 200.

If the phone says *"Can't link new devices right now"*, WhatsApp is rate-limiting the number: wait a few hours before trying again.

To unlink (for example to change the number): `curl -s -X POST -H "X-Api-Key: $HUB_API_KEY" API/api/whatsapp/logout`, then link again.

## Connect Gmail

The Gmail refresh token is created once, on a machine with a browser, with the consent tool:

1. In Google Cloud: enable the Gmail API, create an OAuth client of type **Desktop**, and add the mailbox as a test user. The scope is `gmail.modify` (read, label, send replies).
2. Put `GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET` and `GMAIL_ACCOUNT` (the support address) in `.env`, then run `dotnet run --project src/Intellifer.Hub.Tools.GmailConsent` and sign in as the support mailbox.
3. Put the printed `GMAIL_REFRESH_TOKEN` in `.env` and restart the worker (`./deploy.sh`).

While the Google app's consent screen is in **Testing**, the refresh token expires after 7 days. Publish the app (or use a Workspace internal app) for a token that lasts.

The worker polls the mailbox every 45 seconds, labels stored mail `Support`, and skips mail the mailbox sent itself.

## Connect Odoo

`ODOO_URL`, `ODOO_DB`, `ODOO_LOGIN`, `ODOO_API_KEY` in `.env`. The hub uses Odoo's standard `/jsonrpc` endpoint and only reads:

- `client.contact` — name, company, email, alternate email, phone, project;
- `client.project` — name and code.

Check a sender: `curl -H "X-Api-Key: $HUB_API_KEY" "API/api/customers/lookup?channel=Email&sender=someone@example.com"` (`channel=WhatsApp` with a phone number also works).

## Connect Plane

`PLANE_URL`, `PLANE_API_KEY`, `PLANE_WORKSPACE_SLUG` in `.env`; turn on **Intake** in each Plane project that should receive tickets.

An Odoo client project links to the Plane project whose name or identifier equals the Odoo project's name or code (case, spaces and punctuation ignored). New Plane projects are picked up within 5 minutes. For names that differ: `PLANE_PROJECT_MAP=Odoo name=PLANEIDENTIFIER,…`.

See every link: `curl -H "X-Api-Key: $HUB_API_KEY" API/api/plane/check`.

## Settings

All in `.env` (documented in `.env.example`). The ones most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `PLANE_INTAKE_KEYWORDS` | bug, error, not working, crash, failed, fix, … | Words that make a burst of messages a bug report. |
| `PLANE_WHATSAPP_QUIET_SECONDS` | 120 | Wait this long after the last WhatsApp message before judging the burst. |
| `PLANE_EMAIL_QUIET_SECONDS` | 0 | Same for email. |
| `PLANE_NEW_TICKET_AFTER_IDLE_HOURS` | 24 | An open ticket idle this long gets a new ticket for the next report. |
| `PLANE_ACKNOWLEDGE_INTAKE` | true | Reply with the ticket number. |
| `PLANE_STATUS_REPLIES` | true | Reply when the ticket changes status (checked every `PLANE_STATUS_POLL_MINUTES`). |
| `PLANE_ATTACHMENT_MAX_MB` | 5 | Largest image copied to Plane (Plane's own limit defaults to 5 MB). |
| `REPLY_SIGNATURE` | Intellifer Support | Signs the replies. |

After changing `.env`, run `./deploy.sh` again.

## Operating it

```bash
docker compose -p intellifer ps                          # what is running
docker compose -p intellifer logs -f hub-worker          # Gmail, Plane, replies, images
docker compose -p intellifer logs -f hub-api             # webhooks, API
docker compose -p intellifer down                        # stop (data is kept)
```

Never run `docker compose down -v`: it deletes the database and the saved WhatsApp login.

Back up the database:

```bash
docker compose -p intellifer exec -T hub-db pg_dump -U intellifer intellifer_hub > hub-$(date +%F).sql
```

Look at what happened to recent messages (Skipped rows say why):

```bash
docker compose -p intellifer exec hub-db psql -U intellifer -d intellifer_hub -c \
  "select c.channel, m.sender_normalized, left(m.body,40), m.plane_state, left(m.plane_error,80), c.plane_sequence_id
   from messages m join conversations c on c.id = m.conversation_id order by m.ingested_at desc limit 10;"
```

Queues that retry on their own and keep a reason when they give up: `messages.plane_state` (to Plane), `outbound_messages` (replies), `message_attachments` (images).

## Routes

| Route | Auth |
| --- | --- |
| `POST /webhooks/openwa` | `X-Webhook-Secret` |
| `GET /health/live`, `/health/ready`, `/health/openwa` | none |
| `GET /api/channels/health` | `X-Api-Key` |
| `GET /api/conversations`, `/api/conversations/{id}/messages` | `X-Api-Key` |
| `GET /api/customers/lookup?channel=&sender=` | `X-Api-Key` |
| `GET /api/plane/check` | `X-Api-Key` |

## Development

.NET 10 SDK. `dotnet test Intellifer.Hub.slnx` runs the unit tests. The WhatsApp adapter is `src/Intellifer.Hub.Infrastructure/OpenWa/OpenWaGateway.cs` (REST calls) and `src/Intellifer.Hub.Application/OpenWaEventInterpreter.cs` (webhook payloads).
