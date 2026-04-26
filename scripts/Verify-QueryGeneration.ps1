# E2E check: question -> LLM picks tools -> execute_query returns expected row data (not assistant prose).
# Requires: Docker MySQL (db_agent_test), Ollama, mysql.env with AGENT_USER_PASSWORD, built OllamaMcpBridge.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
if (-not (Test-Path (Join-Path $Root 'OllamaMcpBridge\OllamaMcpBridge.csproj'))) {
    Write-Error "Run from repo root layout: expected OllamaMcpBridge under $Root"
}

$mysqlEnv = Join-Path $Root 'mysql.env'
if (-not (Test-Path $mysqlEnv)) {
    Write-Error "Missing mysql.env at $mysqlEnv"
}

$h = @{}
Get-Content $mysqlEnv | ForEach-Object {
    $l = $_.Trim()
    if ($l -and $l[0] -ne '#') {
        $i = $l.IndexOf('=')
        if ($i -gt 0) { $h[$l.Substring(0, $i).Trim()] = $l.Substring($i + 1) }
    }
}
if (-not $h['AGENT_USER_PASSWORD']) {
    Write-Error 'mysql.env must define AGENT_USER_PASSWORD'
}

$env:ConnectionStrings__MySQL = 'Server=127.0.0.1;Port=3306;Database=db_agent_test;User Id=agent_user;Password=' + $h['AGENT_USER_PASSWORD']
$env:OLLAMA_BRIDGE_EMIT_TOOL_JSONL = '1'

dotnet build -c Debug -v q | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$question = 'Use schema tools first, then run a query that returns how many rows are in the employees table.'
$raw = dotnet run --project OllamaMcpBridge --no-build -c Debug -- $question 2>&1
if ($LASTEXITCODE -ne 0) {
    $raw | Out-Host
    Write-Error "OllamaMcpBridge exited with code $LASTEXITCODE"
}

$toolLines = $raw | Where-Object { $_ -match '^\s*\{' }
if (-not $toolLines) {
    $raw | Out-Host
    Write-Error 'No JSONL tool lines on stdout (expected OLLAMA_BRIDGE_EMIT_TOOL_JSONL=1 output).'
}

$execEvents = @()
foreach ($line in $toolLines) {
    try {
        $o = $line | ConvertFrom-Json
        if ($o.tool -eq 'execute_query') { $execEvents += $o }
    }
    catch {
        Write-Error "Bad JSON line: $line"
    }
}

if ($execEvents.Count -eq 0) {
    Write-Error 'No execute_query tool calls were recorded.'
}

$last = $execEvents[-1]
$sql = [string]$last.arguments.sql
if ($sql -notmatch '(?i)employees') {
    Write-Error "Last SQL should reference employees table; got: $sql"
}

$data = $last.data
if ($data -is [string]) {
    Write-Error "Query did not return JSON rows: $data"
}

$rows = @($data)
if ($rows.Count -lt 1) {
    Write-Error 'Expected at least one result row for the count query.'
}

$row0 = $rows[0]
$nums = @()
if ($row0 -is [System.Management.Automation.PSCustomObject]) {
    $row0.PSObject.Properties | ForEach-Object { if ($_.Value -is [int] -or $_.Value -is [long]) { $nums += [long]$_.Value } }
}
elseif ($row0 -is [hashtable]) {
    foreach ($v in $row0.Values) { if ($v -is [int] -or $v -is [long]) { $nums += [long]$v } }
}

$expected = 50
if ($nums -notcontains $expected) {
    Write-Error "Expected a numeric column value of $expected in first row; got row: $($row0 | ConvertTo-Json -Compress). SQL: $sql"
}

Write-Host "OK: execute_query returned row data consistent with $expected rows (employees). Last SQL: $sql"
