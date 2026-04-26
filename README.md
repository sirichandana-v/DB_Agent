# Db_Agent — Local MySQL MCP + Ollama bridge

On-machine stack: **MySQL 8** ↔ **MCP stdio server (.NET 8)** ↔ **Ollama (`qwen2.5:7b` recommended for tool calling)**. No cloud LLMs in the default path.

## Prerequisites

- .NET 8 SDK
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for the **optional local test database** below)
- [Ollama](https://ollama.com) with `qwen2.5:7b` pulled (`ollama pull qwen2.5:7b`; stronger tool use than many Llama 3.2 builds)

## Local test database (Docker MySQL 8)

Synthetic data only (`example.test` emails, generated names). **`.env` here is only for this optional Docker stack** (root + `agent_user` bootstrap). **Production** uses your existing MySQL and **only** the app connection (e.g. **`ConnectionStrings__MySQL`** with a read-only user)—**no MySQL root password** in application secrets.

**Docker DB credentials** live in **`mysql.env`** (gitignored), not in git.

1. Start Docker Desktop.
2. From the repo root:

   ```powershell
   Copy-Item mysql.env.example mysql.env
   # Edit mysql.env: MYSQL_ROOT_PASSWORD, AGENT_USER_PASSWORD, MYSQL_DATABASE
   ```

   Use **`mysql.env`** (not **`.env`**) for these variables so Docker Compose does not treat **`$`** inside passwords as variable references. You can keep a separate **`.env`** for other tools if you want.

3. First-time bring-up (creates volume, runs `docker/mysql/init/*.sql` and `99-create-agent-user.sh` once):

   ```powershell
   docker compose up -d
   ```

4. **`agent_user` is `SELECT`-only** on `db_agent_test.*` (matches the MCP server’s read-only tools).

5. **Browse tables in the browser (Adminer):** with the stack up, open [http://localhost:8080](http://localhost:8080). Log in with:
   - **System:** MySQL
   - **Server:** `mysql` (Docker service name; already the default)
   - **Username:** `root` (full access) or `agent_user` (read-only)
   - **Password:** from `mysql.env` (`MYSQL_ROOT_PASSWORD` or `AGENT_USER_PASSWORD`)
   - **Database:** `db_agent_test`

6. Connection string for MCP / bridge (PowerShell; use your real `AGENT_USER_PASSWORD` from `mysql.env`):

   ```powershell
   $env:ConnectionStrings__MySQL = "Server=localhost;Port=3306;Database=db_agent_test;User Id=agent_user;Password=YOUR_AGENT_PASSWORD;SslMode=None;"
   ```

7. **Re-run init scripts** only by recreating the data volume (destroys data):

   ```powershell
   docker compose down -v
   docker compose up -d
   ```

8. **End-to-end check** (Ollama running, model pulled, Docker MySQL healthy):

   ```powershell
   cd C:\Users\vscsi\Desktop\siri\Db_Agent
   $env:ConnectionStrings__MySQL = "Server=localhost;Port=3306;Database=db_agent_test;User Id=agent_user;Password=YOUR_AGENT_PASSWORD;SslMode=None;"
   dotnet run --project OllamaMcpBridge -- "How many rows are in the employees table? Use get_schema_context first, then a SELECT."
   ```

**Schema:** `departments`, `employees`, `projects`, `assignments` with FKs — **50 rows per table**. Init order: schema → seed → create `agent_user` + `GRANT SELECT`. After changing SQL init files, recreate the volume (`docker compose down -v && docker compose up -d`) so MySQL re-runs init.

If `99-create-agent-user.sh` fails on Windows, ensure the file uses **LF** line endings (see `.gitattributes`).

## Secrets (do not commit passwords)

**Application (all environments):** set the DB user the MCP server uses—typically a **single** connection string (least privilege, `SELECT` only in production), not MySQL `root`.

Set the connection string via environment (recommended for sensitive data):

- **`ConnectionStrings__MySQL`** — full MySQL connection string (double underscore is intentional for nested config).

Example (PowerShell, current session only):

```powershell
$env:ConnectionStrings__MySQL = "Server=localhost;Port=3306;Database=db_agent_test;User Id=agent_user;Password=***;SslMode=None;"
```

`appsettings.json` may leave `ConnectionStrings:MySQL` empty; environment variables override JSON when both are present.

## Run the MCP server (stdio)

From the repo root:

```powershell
dotnet run --project MySqlMcpServer
```

Logs are written daily under `MySqlMcpServer/logs/` (next to the built output, see `Logging:LogDirectory` in `appsettings.json`): files like `mcp-server-YYYYMMDD.log`.

### MCP tools exposed

| Name                 | Purpose                                        |
| -------------------- | ---------------------------------------------- |
| `get_schema_context` | Full schema text (call before SQL)             |
| `list_tables`        | Comma-separated table names                    |
| `describe_table`     | Columns for one table (`table_name`)           |
| `execute_query`      | Read-only `SELECT` / `WITH` only, max 200 rows |
| `refresh_schema`     | Reload cache after DDL changes                 |

## Ollama bridge (full loop)

The bridge starts the MCP server as a child process, loads tool definitions from MCP, calls Ollama `/api/chat`, and executes tool calls until the model returns a final answer.

From the repo root (so `MySqlMcpServer/MySqlMcpServer.csproj` can be found), with `ConnectionStrings__MySQL` set in the environment **for this shell** (the child MCP process inherits it):

```powershell
cd C:\Users\vscsi\Desktop\siri\Db_Agent
$env:ConnectionStrings__MySQL = "Server=localhost;Port=3306;Database=db_agent_test;User Id=agent_user;Password=***;SslMode=None;"
dotnet run --project OllamaMcpBridge -- "How many tables are in the database?"
```

Configure Ollama URL/model in `OllamaMcpBridge/appsettings.json` or override with environment (e.g. `Ollama__Model`).

### curl → Ollama with all five tools

Tool schemas are generated from the live MCP server; for a static curl example, mirror the following shape (adjust `parameters` to match your MCP tool schemas from `tools/list`):

```bash
curl http://localhost:11434/api/chat -H "Content-Type: application/json" -d "{\"model\":\"qwen2.5:7b\",\"stream\":false,\"messages\":[{\"role\":\"system\",\"content\":\"You are a database assistant. Before querying data, call get_schema_context. Use exact table and column names.\"},{\"role\":\"user\",\"content\":\"List all tables.\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_schema_context\",\"description\":\"Returns ALL tables and columns.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}},{\"type\":\"function\",\"function\":{\"name\":\"list_tables\",\"description\":\"Lists all tables.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}},{\"type\":\"function\",\"function\":{\"name\":\"describe_table\",\"description\":\"Describe one table.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"table_name\":{\"type\":\"string\"}},\"required\":[\"table_name\"]}}},{\"type\":\"function\",\"function\":{\"name\":\"refresh_schema\",\"description\":\"Reload schema cache.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}},{\"type\":\"function\",\"function\":{\"name\":\"execute_query\",\"description\":\"Run read-only SELECT/WITH.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"sql\":{\"type\":\"string\"}},\"required\":[\"sql\"]}}}]}"
```

## Raw MCP over stdio (single `tools/call` example)

MCP uses newline-delimited JSON-RPC. After your client completes the `initialize` handshake, you can call a tool (one JSON object per line written to the server’s stdin). Example body:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": { "name": "list_tables", "arguments": {} }
}
```

You must send `initialize` / `notifications/initialized` per the MCP spec before `tools/call` will succeed; use an MCP-aware client for full sessions.

## Claude Desktop (optional)

Add to `%APPDATA%\Claude\claude_desktop_config.json` under `mcpServers`:

```json
"mysql-local": {
  "command": "dotnet",
  "args": ["run", "--project", "C:/Users/vscsi/Desktop/siri/Db_Agent/MySqlMcpServer"],
  "env": {
    "ConnectionStrings__MySQL": "Server=localhost;Port=3306;Database=db_agent_test;User Id=agent_user;Password=REPLACE_ME;SslMode=None;"
  }
}
```

Use a full path to `MySqlMcpServer.csproj` on your machine; **do not** commit real credentials—set `env` locally or use OS-level secrets.

## Security notes

- `execute_query` allows only statements that start with `SELECT` or `WITH` (after leading comments), rejects multiple statements, and caps rows at **200**.
- Connection strings are never returned from tools.
- All tool invocations and SQL are logged to the daily log files for audit.
