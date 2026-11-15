# 앱·서버·DB 상태 한 번에 보기 (5분 점검용). 읽기만 하고 아무것도 바꾸지 않는다.
#   powershell -ExecutionPolicy Bypass -File verify-status.ps1
$ErrorActionPreference = 'SilentlyContinue'
$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Write-Output "=== $now ==="

# 1) 앱 프로세스 · 창
$app = Get-Process -Name BinanceCumulative -ErrorAction SilentlyContinue | Select-Object -First 1
if ($app) {
    $vis = if ($app.MainWindowHandle -ne 0) { 'visible' } else { 'HIDDEN(tray?)' }
    Write-Output ("app: pid {0} responding={1} window={2} mem={3:N0}MB started={4}" -f $app.Id, $app.Responding, $vis, ($app.WorkingSet64/1MB), $app.StartTime.ToString('HH:mm:ss'))
} else { Write-Output "app: NOT RUNNING" }

# 2) 서버(java) 프로세스
$javas = Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -like '*binance-cumulative-server*' }
foreach ($j in $javas) {
    $jp = Get-Process -Id $j.ProcessId
    Write-Output ("server: pid {0} mem={1:N0}MB cpu={2:N0}s parent={3}" -f $j.ProcessId, ($jp.WorkingSet64/1MB), $jp.CPU, $j.ParentProcessId)
}
if (-not $javas) { Write-Output "server: java NOT RUNNING" }

# 3) health · ranking 요약
try { Write-Output ("health: " + (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 http://localhost:8080/health).Content) } catch { Write-Output "health: FAIL $($_.Exception.Message)" }
try {
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 20 http://localhost:8080/ranking.json
    $j = $r.Content | ConvertFrom-Json
    $gen = [DateTimeOffset]::FromUnixTimeMilliseconds($j.generatedAt).LocalDateTime.ToString('HH:mm:ss')
    $quotes = ($j.coins | Group-Object q | Sort-Object Count -Descending | Select-Object -First 6 | ForEach-Object { "$($_.Name):$($_.Count)" }) -join ' '
    Write-Output ("ranking: {0} coins generated={1} note='{2}' quotes[{3}] bytes={4:N0}" -f $j.coins.Count, $gen, $j.note, $quotes, $r.Content.Length)
} catch { Write-Output "ranking: $($_.Exception.Message)" }
try { Write-Output ("verify: " + ((Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 http://localhost:8080/verify.json).Content | ConvertFrom-Json).note) } catch {}

# 4) 로컬 DB (읽기 전용으로 연다)
$db = "$env:APPDATA\BinanceCumulative\app.db"
if (Test-Path $db) {
    $size = ((Get-Item $db).Length + ((Get-Item "$db-wal").Length)) / 1MB
    $py = @"
import sqlite3, sys, datetime
c = sqlite3.connect(r'file:' + sys.argv[1] + '?mode=ro', uri=True)
q = lambda s: c.execute(s).fetchone()[0]
ms = lambda v: datetime.datetime.fromtimestamp(int(v)/1000).strftime('%m-%d %H:%M') if v else '-'
print('db: settings=%d meta=%s' % (q('select count(*) from settings'), dict(c.execute('select key,value from meta').fetchall())))
print('db: ranking_cache=%s series_cache=%d group_cache=%d' % (ms(q('select fetched_at from ranking_cache')), q('select count(*) from series_cache'), q('select count(*) from group_cache')))
print('db: daily rows=%d symbols=%d methods=%d first=%s last=%s updated=%s' % (q('select count(*) from daily_agg'), q('select count(distinct market||symbol) from daily_agg'), q('select count(distinct method) from daily_agg'), q('select min(day_text) from daily_agg') or '-', q('select max(day_text) from daily_agg') or '-', ms(q('select max(updated_at) from daily_agg'))))
print('db: daily usd_rate null=%d of %d (non-USDT quote rows=%d), final=0 rows=%d' % (q('select count(*) from daily_agg where usd_rate is null'), q('select count(*) from daily_agg'), q("select count(*) from daily_agg where symbol not like '%USDT'"), q('select count(*) from daily_agg where final=0')))
bad = q('select count(*) from daily_agg where abs(buy_quote+sell_quote-amount_quote) > 1e-6*abs(amount_quote)+1e-6 or low>high or amount_quote<-1e-6')
print('db: daily consistency violations=%d' % bad)
"@
    $tmp = Join-Path $env:TEMP 'bc-dbcheck.py'; Set-Content -Path $tmp -Value $py -Encoding UTF8
    python -I $tmp $db 2>&1
    Write-Output ("db: size={0:N1}MB" -f $size)
}

# 5) 오류 로그
$log = "$env:APPDATA\BinanceCumulative\error.log"
if (Test-Path $log) { Write-Output "error.log (tail):"; Get-Content $log -Tail 8 -Encoding UTF8 } else { Write-Output "error.log: none" }

# 6) 데이터 폴더 · 디스크
$data = "$env:LOCALAPPDATA\BinanceCumulative\binance-data"
if (Test-Path $data) {
    $files = Get-ChildItem $data -Recurse -File
    $sym = (Get-ChildItem $data -Recurse -Directory | Where-Object { $_.Parent.FullName -ne $data }).Count
    Write-Output ("data: {0:N0} files {1:N0}MB symbol-dirs={2} newest={3}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum/1MB), $sym, (($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime.ToString('HH:mm:ss')))
}
$drive = Get-PSDrive -Name ($env:LOCALAPPDATA.Substring(0,1))
Write-Output ("disk {0}: free {1:N1}GB" -f $drive.Name, ($drive.Free/1GB))

# 7) 집계·서버 로그 (UTF-8)
foreach ($n in 'aggregate.log','server.log') {
    $p = "$env:APPDATA\BinanceCumulative\$n"
    if (Test-Path $p) { Write-Output "--- $n (tail) ---"; Get-Content $p -Tail 6 -Encoding UTF8 }
}