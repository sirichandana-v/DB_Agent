# Plan (as implemented)

Build a local **MySQL MCP server** in **.NET 8** (C#) plus an **Ollama bridge** console app. **Ollama** runs **llama3.2** at `http://localhost:11434`. Everything runs on-machine by default — no cloud LLMs in the default path.

## Goal

1. Connect to a local **MySQL 8** database (SSL optional; not enabled by default).
2. Expose MCP tools Ollama can call (via the bridge) to inspect schema and run **read-only** queries.
3. Run offline relative to external AI APIs — local Ollama + local MySQL only.

## Local LLM setup

- Ollama (e.g. 0.5.x) with **`llama3.2`** pulled.
- Chat API: `POST http://localhost:11434/api/chat` with `"stream": false` and `"tools": [...]`.
- Tool calling: native for compatible models; the bridge maps Ollama `tool_calls` to MCP `tools/call`.

## Tech stack

- **.NET 8** console apps
- **NuGet:** `ModelContextProtocol` **1.2.x** (official MCP .NET SDK), `MySqlConnector`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Configuration.Json`, `Microsoft.Extensions.Configuration.EnvironmentVariables`
- **Configuration:** `appsettings.json` + **environment variables** (env overrides for secrets)

## Repository layout

```text
Db_Agent/
├── Db_Agent.sln
├── docker-compose.yml
├── .env.example
├── .gitattributes
├── README.md
├── .gitignore
├── docker/
│   └── mysql/
│       └── init/
│           ├── 01-schema.sql
│           ├── 02-seed.sql
│           └── 99-create-agent-user.sh
├── MySqlMcpServer/
│   ├── MySqlMcpServer.csproj
│   ├── appsettings.json
│   ├── Program.cs
│   ├── SqlReadOnlyGuard.cs
│   ├── Logging/
│   │   └── DailyFileLogger.cs
│   ├── Tools/
│   │   ├── ListTablesTool.cs
│   │   ├── DescribeTableTool.cs
│   │   ├── GetSchemaContextTool.cs
│   │   ├── RefreshSchemaTool.cs
│   │   └── ExecuteQueryTool.cs
│   └── Services/
│       ├── DatabaseService.cs
│       └── SchemaCache.cs
└── OllamaMcpBridge/
    ├── OllamaMcpBridge.csproj
    ├── appsettings.json
    └── Program.cs
```

## Configuration

### MySqlMcpServer `appsettings.json`

- **`ConnectionStrings:MySQL`** — may be empty in repo; **production passwords must not be committed**.
- **`ConnectionStrings__MySQL`** (environment) — preferred for sensitive deployments; standard .NET config key for nested values.
- **`SchemaCache:StartupRetries`** — default `3`.
- **`SchemaCache:RetryDelayMs`** — default `2000`.
- **`Logging:LogDirectory`** — e.g. `logs` (under the app base directory unless rooted).
- **`Logging:FileNamePrefix`** — e.g. `mcp-server` → files `mcp-server-YYYYMMDD.log` (**daily rotation**, UTC date in filename).

No Ollama settings in the MCP project; those live in **OllamaMcpBridge**.

### OllamaMcpBridge `appsettings.json`

- **`Ollama:BaseUrl`**, **`Ollama:Model`**, **`Ollama:MaxToolIterations`**
- **`McpServer:Command`** (default `dotnet`), **`McpServer:ProjectFile`** (path to `MySqlMcpServer.csproj`, discoverable upward from cwd)
- **`Agent:SystemPrompt`** — same intent as below (SELECT / WITH, `get_schema_context` first)

## SchemaCache

- Singleton; loads from **`DatabaseService`** using **`INFORMATION_SCHEMA`** (no `SHOW` statements — smaller surface area).
- **`InitializeAsync()`** — fills cache; on failure retries with delay; logs each retry.
- **`RefreshAsync()`** — reloads cache (used by **`refresh_schema`** tool).
- Exposes formatted text for **`get_schema_context`**, CSV table list, per-table column JSON for tools.

## DatabaseService

- Singleton; resolves connection string from configuration (env-aware).
- **`GetTablesAsync()`** — `INFORMATION_SCHEMA.TABLES`, current schema, base tables only.
- **`DescribeTableAsync(tableName)`** — `INFORMATION_SCHEMA.COLUMNS` → JSON array `{ name, type, nullable, key }`.
- **`ExecuteQueryAsync(sql)`** — validates via **`SqlReadOnlyGuard`**, then executes; JSON array of rows (column names as keys); max **200** rows; errors as JSON with message; SQL and operations logged.

## SqlReadOnlyGuard (`ExecuteQueryAsync` / tool)

- After stripping leading `--`, `#`, and `/* */` comments and whitespace, the statement must start with **`SELECT`** or **`WITH`** (case-insensitive).
- **Multiple statements rejected** (semicolon outside quotes with non-empty remainder).
- Does **not** allow `SHOW`, `INSERT`, `UPDATE`, `DELETE`, etc.

## Logging

- **`DailyFileLogger`**: one log file per UTC calendar day; format `[yyyy-MM-dd HH:mm:ssZ] [LEVEL] message`.
- **Console logging disabled** on the MCP host so stdio JSON-RPC is not corrupted.
- Tool entry points and **`DatabaseService`** log tool usage and SQL where applicable.

## MCP tools (five)

All use **`[McpServerToolType]`** / **`[McpServerTool]`** and are discovered with **`WithToolsFromAssembly`**.

| Name | Input | Behavior |
|------|--------|----------|
| `list_tables` | none | Comma-separated names from cache |
| `describe_table` | `table_name` | JSON columns from cache |
| `get_schema_context` | none | Full schema text from cache (primary grounding) |
| `refresh_schema` | none | Reloads cache from MySQL after DDL changes |
| `execute_query` | `sql` | Read-only SELECT/WITH only; JSON rows, max 200 |

## MySqlMcpServer `Program.cs`

- **`Host.CreateApplicationBuilder`**, **`Logging.ClearProviders()`**.
- Register **`DailyFileLogger`**, **`DatabaseService`**, **`SchemaCache`** as singletons.
- **`AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly(...)`**.
- After **`Build()`**, **`await SchemaCache.InitializeAsync()`** then **`RunAsync()`**.
- Log startup and schema load to the daily file.

## Ollama bridge (`OllamaMcpBridge`)

- Loads config; resolves path to **`MySqlMcpServer.csproj`**.
- Starts MCP server child via **`StdioClientTransport`** (`dotnet run --project ...`); child inherits environment (**`ConnectionStrings__MySQL`** must be set in the parent shell for local runs).
- **`McpClient.CreateAsync`** → **`ListToolsAsync`** → builds Ollama **`tools`** array from live MCP schemas.
- Loop: POST **`/api/chat`** → on **`tool_calls`**, **`CallToolAsync`** → append **`role: tool`** messages → repeat until no tool calls or max iterations.
- User question: CLI args (see **README.md**).

## Ollama request shape (illustrative)

`POST http://localhost:11434/api/chat` with `model`, `messages`, `tools` (all **five** tools with schemas from MCP), `"stream": false`. Exact JSON is produced by the bridge or can be mirrored in **curl** (see **README.md**).

## Claude Desktop (optional)

`mcpServers` entry: `dotnet run --project <full-path-to-MySqlMcpServer.csproj>`, with **`env.ConnectionStrings__MySQL`** — never commit real secrets.

## System prompt (Ollama / bridge)

The bridge default matches this intent: you are a database assistant; **always call `get_schema_context` first**; then use **exact** table/column names; use precise **`SELECT`** or **`WITH`** queries; never guess column names.

## Safety rules (implemented)

- Only **SELECT** / **WITH** queries; no `SHOW`; no multi-statement batches.
- Never return the connection string from tools.
- **200-row** cap on query results.
- Audit trail via daily log files and tool/SQL logging.

## Local test database (Docker MySQL 8)

- **Scope:** dev/test only. **Production** uses an existing MySQL with a least-privilege user via **`ConnectionStrings__MySQL`** only—**no** Docker `.env`, **no** application secret for MySQL `root`.
- **`docker-compose.yml`** — `mysql:8`, port **3306**, database **`db_agent_test`**, named volume **`mysql_data`**.
- **Init** (`docker/mysql/init/`): **`01-schema.sql`** → **`02-seed.sql`** (50 rows per table, ASCII synthetic data) → **`99-create-agent-user.sh`** creates **`agent_user`** with **`GRANT SELECT` only** on `db_agent_test.*`.
- **Secrets:** `.env` from **`.env.example`** (`MYSQL_ROOT_PASSWORD`, `AGENT_USER_PASSWORD`); **`.env` is gitignored**.
- **Connection (tests):** `localhost:3306`, **`SslMode=None`**, user **`agent_user`**, DB **`db_agent_test`**.
- **Reset DB:** `docker compose down -v` then `up` again (replays init only on empty volume).
- **E2E:** Docker MySQL + **`ConnectionStrings__MySQL`** + Ollama with **`llama3.2`** + **`OllamaMcpBridge`** (see **README.md**).

## Deliverables

1. **Source** — `MySqlMcpServer` + `OllamaMcpBridge` as above; **`dotnet build Db_Agent.sln`** succeeds.
2. **README.md** — connection string via env; `dotnet run` for MCP; bridge usage; **curl** to Ollama with **five** tools; one **raw MCP `tools/call`** line (with note that `initialize` precedes it); Claude Desktop snippet; **Docker test DB** bring-up; `.gitignore` for `bin/`, `obj/`, `logs/`, `.env`.
3. **Docker test assets** — `docker-compose.yml`, `docker/mysql/init/*`, `.env.example`, `.gitattributes` (LF for `*.sh`).
