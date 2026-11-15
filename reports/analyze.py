"""최근 1년 매집 분석 — DB(daily_agg, 그날 환율 USDT) 기준. 읽기 전용.
사용: python analyze.py <out_dir>
"""
import sqlite3, json, sys, os, datetime, collections, math

OUT = sys.argv[1]
DAY = 86400000
UTC = datetime.timezone.utc
def ms(y, m, d): return int(datetime.datetime(y, m, d, tzinfo=UTC).timestamp() * 1000)
def dt(v): return datetime.datetime.fromtimestamp(v / 1000, UTC)

END = ms(2026, 11, 13)          # 마지막으로 마감된 날 (UTC)
START = ms(2025, 11, 14)        # 365일 창: 2025-11-14 ~ 2026-11-13
PREV_START = ms(2024, 11, 14)   # 직전 1년 (비교용)
METHOD = 'TAKER'
EXC = {"BTC", "USDT", "USDC", "FDUSD", "TUSD", "USD1", "USDE", "USDS", "RLUSD", "BFUSD", "RWUSD", "XUSD", "U", "BUSD", "USDP", "PAX", "DAI",
       "PYUSD", "UST", "EUR", "EURI", "AEUR", "WBTC", "WBETH", "BETH", "BNSOL", "WETH", "STETH"}

db = sqlite3.connect('file:E:/BinanceCumulative/app.db?mode=ro', uri=True)
listing = {s: f for m, s, f in db.execute('select market,symbol,first_ms from pair_listing')}
rk = json.load(open(os.path.join(OUT, 'rk.json'), encoding='utf-8-sig'))
sup = {}
for c in rk['coins']:
    if c.get('sup') and c['sup'].get('circ'):
        sup[c['b']] = (float(c['sup']['circ']), c['sup'].get('name', ''))

rows = db.execute("""select symbol, base, quote, base_mult, day, net_usd, buy_usd, sell_usd, amount_usd, net_base, close, usd_rate, net_quote, amount_quote
                     from daily_agg where market='SPOT' and method=? and day>=? and day<=?""", (METHOD, PREV_START, END)).fetchall()
print('rows', len(rows), 'symbols', len({r[0] for r in rows}))

# 페어 완결성: 창 안의 날 수 (상장일 이후 모든 날이 있어야 함)
pair_days = collections.Counter(); pair_info = {}
for sym, base, quote, mult, day, *_ in rows:
    if day >= START: pair_days[sym] += 1
    pair_info[sym] = (base, quote, mult)
def expected_days(sym):
    f = listing.get(sym)
    if f is None: return None
    s = max(START, (f // DAY) * DAY)
    return max(0, (END - s) // DAY + 1)
incomplete = {s for s in pair_info if expected_days(s) is not None and pair_days[s] < expected_days(s) - 1}
print('pairs in window', len(pair_days), 'incomplete', len(incomplete), sorted(incomplete)[:10])

coin = collections.defaultdict(lambda: dict(net=0.0, buy=0.0, sell=0.0, amt=0.0, netc=0.0, prev_net=0.0, prev_amt=0.0, nousd=0,
                                            month=collections.defaultdict(float), week=collections.defaultdict(float), dayn=collections.defaultdict(float),
                                            quote=collections.defaultdict(float), quote_amt=collections.defaultdict(float), pairs=set(),
                                            px={}, first=None))
market_month = collections.defaultdict(lambda: dict(net=0.0, amt=0.0))
coin_month = collections.defaultdict(lambda: collections.defaultdict(float))
for sym, base, quote, mult, day, net_usd, buy_usd, sell_usd, amt_usd, net_base, close, rate, net_q, amt_q in rows:
    e = coin[base]
    f = listing.get(sym)
    if f is not None: e['first'] = f if e['first'] is None else min(e['first'], f)
    if net_usd is None:
        if day >= START: e['nousd'] += 1
        continue
    if day < START:
        e['prev_net'] += net_usd; e['prev_amt'] += amt_usd
        continue
    d = dt(day); mk = d.strftime('%Y-%m')
    e['net'] += net_usd; e['buy'] += buy_usd; e['sell'] += sell_usd; e['amt'] += amt_usd; e['netc'] += net_base * mult
    e['month'][mk] += net_usd; e['week'][(day - START) // (7 * DAY)] += net_usd; e['dayn'][day] += net_usd
    e['quote'][quote] += net_usd; e['quote_amt'][quote] += amt_usd; e['pairs'].add(sym)
    if quote == 'USDT': e['px'][day] = close / mult
    market_month[mk]['net'] += net_usd; market_month[mk]['amt'] += amt_usd
    coin_month[mk][base] += net_usd

res = []
for b, e in coin.items():
    if e['amt'] <= 0: continue
    px = sorted(e['px'].items())
    p0 = px[0][1] if px else None; p1 = px[-1][1] if px else None
    months = sorted(e['month'].items())
    weeks = [v for _, v in sorted(e['week'].items())]
    days = sorted(e['dayn'].items())
    pos_days = sum(v for _, v in days if v > 0)
    top5 = sum(sorted((v for _, v in days), reverse=True)[:5])
    circ = sup.get(b, (None, ''))[0]
    res.append(dict(
        base=b, name=sup.get(b, (None, ''))[1], excluded=b in EXC,
        net=e['net'], buy=e['buy'], sell=e['sell'], amt=e['amt'], imb=e['net'] / e['amt'],
        net_coins=e['netc'], circ=circ, pct_supply=(e['netc'] / circ * 100) if circ else None,
        mcap=(circ * p1) if circ and p1 else None, net_mcap=(e['net'] / (circ * p1) * 100) if circ and p1 else None,
        prev_net=e['prev_net'], prev_amt=e['prev_amt'],
        months=months, pos_months=sum(1 for _, v in months if v > 0), n_months=len(months),
        pos_weeks=sum(1 for v in weeks if v > 0), n_weeks=len(weeks),
        top5_share=(top5 / e['net']) if e['net'] > 0 else None,
        p0=p0, p1=p1, px_chg=((p1 / p0 - 1) * 100) if p0 and p1 else None,
        px_min=min(v for _, v in px) if px else None, px_max=max(v for _, v in px) if px else None,
        quotes=sorted(e['quote'].items(), key=lambda kv: -abs(kv[1])), quote_amt=dict(e['quote_amt']),
        pairs=sorted(e['pairs']), n_incomplete=sum(1 for s in e['pairs'] if s in incomplete), nousd_days=e['nousd'],
        first=e['first'], listed_before=(e['first'] is not None and e['first'] <= START),
        px_series=[(d, v) for d, v in px], day_net=[(d, v) for d, v in days],
    ))
json.dump(dict(start=START, end=END, generated=datetime.datetime.now().isoformat(timespec='seconds'), n_symbols=len(pair_days), incomplete=sorted(incomplete),
               coins=res, market_month={k: v for k, v in sorted(market_month.items())},
               breadth={k: dict(pos=sum(1 for b, v in cm.items() if v > 0 and b not in EXC), n=sum(1 for b in cm if b not in EXC)) for k, cm in sorted(coin_month.items())}),
          open(os.path.join(OUT, 'analysis.json'), 'w'))

elig = [r for r in res if not r['excluded'] and r['listed_before'] and r['amt'] >= 1e8 and r['n_incomplete'] == 0]
print('coins', len(res), 'eligible', len(elig), 'net>0', sum(1 for r in elig if r['net'] > 0))
tot = sum(r['net'] for r in res); print('market net %.4g  vol %.4g' % (tot, sum(r['amt'] for r in res)))
def line(r):
    return (f"  {r['base']:8} net {r['net']/1e8:8.2f}억 vol {r['amt']/1e8:9.1f}억 imb {r['imb']*100:6.2f}% +mon {r['pos_months']:2}/{r['n_months']} +wk {r['pos_weeks']:2}/{r['n_weeks']} "
            f"top5d {('%5.0f%%' % (r['top5_share']*100)) if r['top5_share'] else '   - '} px {('%+7.1f%%' % r['px_chg']) if r['px_chg'] is not None else '    -  '} "
            f"net/mcap {('%6.2f%%' % r['net_mcap']) if r['net_mcap'] is not None else '   -  '} prev {r['prev_net']/1e8:8.2f}억 q {','.join(q for q, _ in r['quotes'][:4])}")
for title, key in [('TOP net USDT', lambda r: r['net']), ('TOP imbalance', lambda r: r['imb']), ('TOP net/mcap', lambda r: r['net_mcap'] if r['net_mcap'] is not None else -1e9)]:
    print('\n' + title)
    for r in sorted(elig, key=key, reverse=True)[:15]: print(line(r))
print('\nBOTTOM net USDT')
for r in sorted(elig, key=lambda r: r['net'])[:8]: print(line(r))
print('\nexcluded (BTC etc):')
for r in sorted((r for r in res if r['excluded']), key=lambda r: r['net'])[:14]: print(line(r))
print('\nmarket by month:')
for k, v in sorted(market_month.items()): print(' ', k, 'net %9.2f억 vol %10.1f억 imb %6.2f%%' % (v['net'] / 1e8, v['amt'] / 1e8, v['net'] / v['amt'] * 100))
