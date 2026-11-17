# Exports and Secure Destinations

CrawlForge exports your audit data to local files in several formats, and can optionally deliver those exports to destinations you configure yourself. This guide covers what each export format contains, the export job workflow, and how to configure encrypted-at-rest delivery destinations with explicit authorization.

The governing principle is local-first:

- Exports are written to your own filesystem.
- Outbound delivery happens **only** to destinations you explicitly create and authorize.
- Delivery uses **your own credentials**, never vendor accounts.
- There is no telemetry and no outbound traffic by default.
- Disabling or deleting all destinations restores the guarantee of zero non-crawl outbound traffic.

## Export formats

CrawlForge produces four local export formats. Each is generated from the audit's data through the backend; nothing is assembled in the page.

| Format | What it contains |
|---|---|
| Spreadsheet workbook | A multi-sheet workbook: summary, URLs, findings, links, and custom extraction (when enabled) |
| CSV | One CSV file per dataset |
| HTML report bundle | A self-contained HTML report you can open offline or share as a single bundle |
| PDF summary | An optional PDF summary of the audit |

### Spreadsheet workbook

A single workbook with separate sheets:

| Sheet | Contents |
|---|---|
| Summary | The audit's summary metrics |
| URLs | The URL dataset |
| Findings | The findings dataset |
| Links | The link dataset |
| Custom extraction | Your extraction dataset (included when custom extraction is enabled) |

This is the most complete single-file export.

### CSV per dataset

- Each dataset is exported as its own CSV file.
- Suitable for importing into other tools or pipelines.
- Datasets are separate files rather than sheets in one workbook.

### HTML report bundle

- Self-contained: it carries what it needs to render offline.
- No remote content, fonts, or scripts.
- Open it in a browser or share it as a single bundle.

### PDF summary

- An optional summarized view of the audit.
- Useful for sharing a high-level report.

### Custom extraction column expansion

Custom extraction exports use the **same dynamic-column expansion as the explorer**:

- Multi-valued rules expand to the busiest page's column count.
- The exported columns match what you see in the custom extraction explorer.
- No value is hidden by a fixed column limit.

### Additional datasets

Beyond the core datasets, exports can include the additive module datasets:

- **WAF blocks**
- **N-grams**
- **Redirect-matrix** results
- **Semantic link opportunities**
- **Analytics** detections

Select the datasets you want when you create the export.

### What the core datasets contain

| Dataset | Typical fields |
|---|---|
| Summary | Counts by status/severity/category and crawl-health metrics |
| URLs | Status and timing, indexability, depth, content type, canonical URL |
| Findings | Rule key, severity, category, URL, and evidence |
| Links | Source, target, anchor text, relation flags, internal/external marker, DOM zone, weighted-equity fields |
| Custom extraction | Dynamic per-rule columns (multi-valued rules expanded to the busiest page) |

The additive datasets carry their module's records: WAF block evidence, n-gram frequencies, redirect-matrix verdicts and hop chains, semantic link opportunities (source, target, similarity, anchor candidates), and analytics detections.

> **Note:** The export reflects your current selection and filter state. If you filtered an explorer or selected rows, that state is handed to the export flow so you can export exactly the slice you are looking at — not necessarily the whole audit.

## The export job workflow

Exports are jobs, not blocking operations. When you request an export, the backend returns a job identifier immediately and the work proceeds in the background; progress flows over the local real-time hub.

### The lifecycle

1. **Create the export** — choose the audit, the format, and the dataset selection (and your selection/filter scope). The backend creates an export job and returns its identifier right away.
2. **Track progress** — the job's progress is reported over the hub while the file is generated. Large exports (for example, a 500,000-row URL sheet) do not freeze the interface.
3. **Download the file** — when the job completes, the file is streamed to you as a local download. The shell provides the native file dialog so you choose where to save it.

### The local API behind the workflow

You normally interact with the export UI rather than calling these directly, but the underlying local endpoints are:

| Operation | Endpoint |
|---|---|
| Create export job | `POST /api/exports` (audit reference, format, dataset selection) |
| Download the file | `GET /api/exports/{id}/file` (local file stream) |

Both follow the local API conventions: loopback only, JSON envelope, and immediate return with an identifier for long operations.

### Generating an export

1. Open the audit you want to export.
2. (Optional) Apply filters or select rows in an explorer to scope the export to a slice.
3. Open the export flow and choose the **format** (workbook, CSV, HTML bundle, or PDF summary).
4. Choose the **datasets** to include (summary, URLs, findings, links, custom extraction, and any additive datasets).
5. Start the export. The job begins immediately; watch its progress in the UI.
6. When complete, save the file through the native file dialog.

> **Tip:** For a complete handoff, export the multi-sheet workbook — it carries summary, URLs, findings, links, and custom extraction in one file. For feeding another tool, export CSV per dataset.

## Optional export destinations

By default, **no destinations exist**. Export delivery to anywhere other than your local filesystem is entirely opt-in: you must create a destination before any transfer can occur.

### Destination classes

CrawlForge supports three destination classes, all addressed with **your own credentials, never vendor accounts**:

| Destination class | What it is |
|---|---|
| Cloud drive / spreadsheet service | Your own cloud drive or spreadsheet account |
| Object-storage endpoint | Your own object storage (for example, an S3-compatible bucket) |
| Notification webhook | Your own webhook endpoint (for example, a team notification hook) |

These are your services, not vendor-operated cloud services. CrawlForge never supplies accounts or credentials of its own, and there is no vendor cloud involved in delivery.

### What a destination stores

Each destination stores:

- A **user-chosen name**.
- The **class**.
- **Endpoint details**.
- A **credential payload encrypted at rest**.

### Configuring a destination

Destinations are managed in settings (export-destination management), with test-connection feedback.

1. Open **settings → export destinations**.
2. Create a new destination: give it a **name**, choose its **class**, and enter the **endpoint details**.
3. Provide the **credential payload** for your service. It is encrypted at rest immediately.
4. Run the **test-connection** action. It verifies reachability and credentials before first use and reports a verdict.
5. Save the destination. It now appears as an available delivery target when you export.

You can update or delete a destination at any time. The destination list and details never return credentials in clear text.

### Testing a connection

The test-connection action is the safety check before first use:

1. Open the destination's test-connection action.
2. CrawlForge attempts to reach the endpoint and authenticate with your credentials.
3. It reports a reachability and credential verdict.
4. Resolve any failure before relying on the destination.

> **Warning:** Always run test-connection before relying on a destination. It confirms both that the endpoint is reachable and that your credentials are valid, so a scheduled delivery does not fail silently later.

### The destination API

The local endpoints behind destination management are:

| Operation | Endpoint |
|---|---|
| List / create destinations | `GET`, `POST /api/destinations` |
| Update / delete a destination | `PUT`, `DELETE /api/destinations/{id}` |
| Test a destination | `POST /api/destinations/{id}/test` |

Credentials are never returned in clear by any of these.

## Credential handling

Credentials are protected by a mandatory secret-protection scheme.

### What is encrypted

The following secrets are encrypted at rest:

- Destination credentials.
- Site credentials.
- External-database credentials.
- Proxy credentials.
- Any local token or signing secrets.

### How encryption works

- Encryption uses **app-generated entropy persisted locally**.
- Key material is **never derived from hardware or OS identity**.
- Secrets are used solely for their respective outbound connections.

### What you see in the UI

- Credentials are **never returned in clear** by the settings or destination APIs.
- The UI shows that a credential is configured, not the secret itself.

This means your secrets stay on your machine, encrypted, and are used only for the outbound connection to the destination you configured.

## Authorizing outbound delivery

A configured destination does not send anything on its own. Transfers occur **only** when:

- **You initiate an export to the destination**, or
- **You pre-authorize it as a schedule completion action**.

There is no automatic or background delivery. With no destinations configured (the default), CrawlForge generates zero non-crawl outbound traffic.

### Pre-authorizing delivery on a schedule

If you want a recurring audit to deliver its report automatically:

1. Configure the destination first (and test it).
2. Edit the schedule and set the destination as a **completion action**.
3. The scheduled audit then delivers its export to that destination when it completes.

This is an explicit, per-schedule authorization you set up in advance. You can revoke it at any time by editing the schedule or disabling/deleting the destination.

> **Note:** Even pre-authorized delivery only ever reaches the destinations you configured. It never contacts a vendor service, and it never sends diagnostics or metrics.

## Delivery failures and resilience

Destination delivery is designed never to jeopardize your audit or your local export:

- Delivery failures are **retried with bounded backoff**.
- Failures are **logged locally**.
- Failures are **surfaced in the UI**.
- A destination failure **never fails the audit**.
- A destination failure **never fails the local export**.

So if a webhook or bucket is temporarily unreachable:

1. Your audit completes normally.
2. Your local file is still produced.
3. The delivery is retried with bounded backoff.
4. You are informed of the failure in the UI and local logs.

## The network boundary

Export delivery sits inside CrawlForge's strict network boundary. Outbound traffic is limited to:

| Outbound class | When it occurs |
|---|---|
| Crawl targets | HTTP(S) requests to crawl targets, including robots files, sitemaps, and page resources; optionally through a user-configured proxy |
| Local file writes | User-initiated exports to your filesystem |
| Export destinations | Transfers to destinations you explicitly configured, only when you initiate them or pre-authorize them on a schedule |
| External database | Connections to a user-configured external database server, only when that provider is selected |

There is:

- **No telemetry.**
- **No remote crash reporting.**
- **No update checking.**
- **No account service.**
- **No vendor-operated cloud service** of any kind.

Diagnostic artifacts (logs, minidumps) and metrics never leave the machine. All local listeners are loopback-only.

Disabling or deleting all destinations restores the guarantee of zero non-crawl outbound traffic — leaving only the crawl itself and your local file exports.

### Air-gapped use

For air-gapped or fully offline use:

1. Never create a destination.
2. Use the default local file-based database.
3. Export to local files only.

CrawlForge then performs no outbound traffic beyond the crawl targets you choose, and all reports and exports remain fully local and offline.

> **Tip:** The absence of accounts, licenses, update checking, and vendor cloud means an air-gapped installation is a supported posture, not a workaround.

## Choosing a format

Pick the format that matches what you will do with the export:

| Goal | Recommended format |
|---|---|
| Complete single-file handoff of an audit | Spreadsheet workbook |
| Feed URLs/findings/links into another tool or pipeline | CSV per dataset |
| Share a report that opens offline in a browser | HTML report bundle |
| Share a high-level summary document | PDF summary |
| Move custom extraction values with all multi-valued columns | Workbook or CSV (dynamic-column expansion) |

## Managing existing destinations

You can change or remove a destination at any time:

1. Open **settings → export destinations**.
2. Select the destination to edit or delete.
3. To edit: update the name, endpoint details, or credential payload, then re-run test-connection.
4. To delete: remove the destination. Any schedule completion action that referenced it no longer delivers there.

Remember:

- Credentials are never shown in clear when you view or edit a destination.
- Removing all destinations restores zero non-crawl outbound traffic.
- A deleted destination cannot receive deliveries until you recreate it.

## Troubleshooting delivery

If a delivery fails, work through these checks (failures are logged locally and shown in the UI):

1. **Read the UI failure notice** — it surfaces the delivery failure and is recorded in local logs.
2. **Run test-connection** on the destination to check reachability and credential validity.
3. **Verify the endpoint details** — a changed bucket, URL, or webhook target is a common cause.
4. **Verify the credential** — rotate or re-enter it if it has expired or been revoked, then re-test.
5. **Let the retry run** — delivery is retried with bounded backoff; a transient outage may resolve on its own.

Throughout, your audit and your local export are unaffected: a destination failure never fails either.

## Key guarantees

| Guarantee | What it means |
|---|---|
| Local-first | Exports write to your filesystem; reports are fully offline |
| Off by default | No destination exists until you create one |
| Your credentials | Destinations use your own credentials, never vendor accounts |
| Encrypted at rest | Credential payloads are encrypted with locally persisted app-generated entropy |
| Explicit authorization | Transfers occur only on your initiation or a pre-authorized schedule action |
| Audit safety | A destination failure never fails the audit or the local export |
| No vendor traffic | No telemetry, crash reporting, update checking, accounts, or vendor cloud |
| Restorable boundary | Disabling/deleting all destinations restores zero non-crawl outbound traffic |

## Related guides

- [Dashboards and virtualized explorers](dashboards-and-virtualized-explorers.md) — selection and filter state that flows into the export, and the explorer surfaces behind each dataset.
- [Link graphs and treemaps](link-graphs-and-treemaps.md) — the link dataset and equity fields you can export.
- [N-grams and topical density](../05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) — the n-gram dataset available as an additive export.

[← Documentation portal](../README.md)