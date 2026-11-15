"""조용한 매집 분석: BTC 로 나눈 가격이 저점권이고, 누적 순매수(C 체결, 그날 환율 USDT)가 꾸준히 느는 코인.
읽기 전용. 사용: python quiet.py <out_dir>
"""
import sqlite3, json, sys, os, datetime, collections, math, statistics

OUT = sys.argv[1]
DAY = 86400000
UTC = datetime.timezone.utc
def ms(y, m, d): return int(datetime.datetime(y, m, d, tzinfo=UTC).timestamp() * 1000)
def ds(v): return datetime.datetime.fromtimestamp(v / 1000, UTC).strftime('%Y-%m-%d')

END = ms(2026, 11, 13)            # 마지막으로 마감된 날
START = END - 364 * DAY           # 최근 1년 (365일): 2025-11-14 ~
HIST = END - (730 - 1) * DAY      # 저점 판단용 2년
H6 = END - 181 * DAY              # 최근 6개월
D90 = END - 89 * DAY
D30 = END - 29 * DAY
EXC = {"BTC", "USDT", "USDC", "FDUSD", "TUSD", "USD1", "USDE", "USDS", "RLUSD", "BFUSD", "RWUSD", "XUSD", "U", "BUSD", "USDP", "PAX", "DAI",
       "PYUSD", "UST", "EUR", "EURI", "AEUR", "WBTC", "WBETH", "BETH", "BNSOL", "WETH", "STETH"}

db = sqlite3.connect('file:E:/BinanceCumulative/app.db?mode=ro', uri=True)
listing = {s: f for m, s, f in db.execute('select market,symbol,first_ms from pair_listing')}
rk = json.load(open(os.path.join(OUT, 'rk.json'), encoding='utf-8-sig'))
sup = {c['b']: (float(c['sup']['circ']), c['sup'].get('name', '')) for c in rk['coins'] if c.get('sup') and c['sup'].get('circ')}

btc = {d: c for d, c in db.execute("select day, close from daily_agg where symbol='BTCUSDT' and method='TAKER' and day>=? and day<=?", (HIST, END))}
print('btc days', len(btc), ds(min(btc)), ds(max(btc)))

rows = db.execute("""select symbol, base, quote, base_mult, day, net_usd, amount_usd, close, usd_rate
                     from daily_agg where market='SPOT' and method='TAKER' and day>=? and day<=? and final=1""", (HIST, END)).fetchall()
print('rows', len(rows), 'symbols', len({r[0] for r in rows}))

C = collections.defaultdict(lambda: dict(net=collections.defaultdict(float), amt=collections.defaultdict(float), px={}, pxvol={}, pairs=set(), first=None, nousd=0))
for sym, base, quote, mult, day, net, amt, close, rate in rows:
    e = C[base]
    e['pairs'].add(sym)
    f = listing.get(sym)
    if f is not None: e['first'] = f if e['first'] is None else min(e['first'], f)
    if net is None or rate is None:
        e['nousd'] += 1
        continue
    e['net'][day] += net; e['amt'][day] += amt
    # 대표 가격: 그날 거래대금이 가장 큰 페어의 종가를 USDT 로
    if amt > e['pxvol'].get(day, -1):
        e['pxvol'][day] = amt; e['px'][day] = close * rate / mult

def linreg_r2(ys):
    n = len(ys)
    if n < 3: return 0.0, 0.0
    mx = (n - 1) / 2; my = sum(ys) / n
    sxx = sum((i - mx) ** 2 for i in range(n)); sxy = sum((i - mx) * (y - my) for i, y in enumerate(ys)); syy = sum((y - my) ** 2 for y in ys)
    if sxx == 0 or syy == 0: return 0.0, 0.0
    return sxy / sxx, (sxy * sxy) / (sxx * syy)

def stdev(xs): return statistics.pstdev(xs) if len(xs) > 1 else 0.0
def corr(a, b):
    n = len(a)
    if n < 10: return None
    ma, mb = sum(a) / n, sum(b) / n
    sa = math.sqrt(sum((x - ma) ** 2 for x in a)); sb = math.sqrt(sum((y - mb) ** 2 for y in b))
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / (sa * sb) if sa > 0 and sb > 0 else None

days1y = [START + i * DAY for i in range(365)]
out = []
for b, e in C.items():
    net1 = [e['net'].get(d, 0.0) for d in days1y]
    amt1 = [e['amt'].get(d, 0.0) for d in days1y]
    tot_net, tot_amt = sum(net1), sum(amt1)
    if tot_amt <= 0: continue
    have_days = sum(1 for d in days1y if d in e['amt'])
    # 누적 순매수
    cum = []; s = 0.0
    for v in net1: s += v; cum.append(s)
    slope, r2 = linreg_r2(cum)
    peak = -1e300; mdd = 0.0
    for v in cum:
        peak = max(peak, v); mdd = max(mdd, peak - v)
    weeks = [sum(net1[i:i + 7]) for i in range(0, 364, 7)]
    months = collections.OrderedDict()
    for d, v in zip(days1y, net1): months[ds(d)[:7]] = months.get(ds(d)[:7], 0.0) + v
    top5 = sum(sorted(net1, reverse=True)[:5])
    net6 = sum(e['net'].get(d, 0.0) for d in days1y if d >= H6); net90 = sum(e['net'].get(d, 0.0) for d in days1y if d >= D90)
    # 가격 (USDT) 과 BTC 대비 가격
    pxd = sorted((d, p) for d, p in e['px'].items() if p > 0 and d in btc and btc[d] > 0)
    if len(pxd) < 60: continue
    ratio = [(d, p / btc[d]) for d, p in pxd]
    r1y = [(d, v) for d, v in ratio if d >= START]
    if len(r1y) < 60: continue
    r_now = sum(v for _, v in ratio[-7:]) / len(ratio[-7:])     # 최근 7일 평균 (하루 튐 완화)
    def pos(series):
        lo, hi = min(v for _, v in series), max(v for _, v in series)
        return (r_now - lo) / (hi - lo) if hi > lo else 0.5
    p_now = pxd[-1][1]; p_1y = next((p for d, p in pxd if d >= START), None)
    usd1y = [(d, p) for d, p in pxd if d >= START]
    ratio_chg = (r1y[-1][1] / r1y[0][1] - 1) * 100
    r90 = [v for d, v in ratio if d >= D90]
    lr = [math.log(r90[i] / r90[i - 1]) for i in range(1, len(r90)) if r90[i] > 0 and r90[i - 1] > 0]
    # BTC 와 같이 움직였나: 최근 180일 일간 수익률 상관·베타 (USDT 가격 기준)
    p180 = [(d, p) for d, p in pxd if d >= H6]
    ra = []; rb = []
    for i in range(1, len(p180)):
        d0, d1 = p180[i - 1][0], p180[i][0]
        if d1 - d0 == DAY: ra.append(math.log(p180[i][1] / p180[i - 1][1])); rb.append(math.log(btc[d1] / btc[d0]))
    cr = corr(ra, rb)
    vb = sum((y - sum(rb) / len(rb)) ** 2 for y in rb) if rb else 0
    beta = (sum((x - sum(ra) / len(ra)) * (y - sum(rb) / len(rb)) for x, y in zip(ra, rb)) / vb) if vb > 0 else None
    amt30 = [e['amt'].get(d, 0.0) for d in days1y if d >= D30]
    med_amt = statistics.median(amt1) if amt1 else 0
    circ = sup.get(b, (None, ''))[0]
    out.append(dict(
        base=b, name=sup.get(b, (None, ''))[1], excluded=b in EXC, pairs=sorted(e['pairs']), first=e['first'],
        listed_1y=(e['first'] is not None and e['first'] <= START), listed_2y=(e['first'] is not None and e['first'] <= HIST), have_days=have_days,
        net=tot_net, amt=tot_amt, imb=tot_net / tot_amt, net6=net6, net90=net90,
        pos_weeks=sum(1 for w in weeks if w > 0) / len(weeks), pos_months=sum(1 for v in months.values() if v > 0), n_months=len(months),
        months=list(months.items()), r2=r2, slope_pos=slope > 0, mdd_share=(mdd / tot_net) if tot_net > 0 else None, top5_share=(top5 / tot_net) if tot_net > 0 else None,
        px_now=p_now, px_chg=((p_now / p_1y - 1) * 100) if p_1y else None,
        usd_pos_1y=((p_now - min(p for _, p in usd1y)) / (max(p for _, p in usd1y) - min(p for _, p in usd1y))) if max(p for _, p in usd1y) > min(p for _, p in usd1y) else 0.5,
        ratio_chg=ratio_chg, ratio_pos_1y=pos(r1y), ratio_pos_2y=pos(ratio), ratio_range90=(max(r90) / min(r90)) if r90 and min(r90) > 0 else None,
        ratio_vol90=stdev(lr) * math.sqrt(365) * 100 if lr else None, corr_btc=cr, beta_btc=beta,
        vol30_vs_med=(statistics.median(amt30) / med_amt) if med_amt > 0 else None,
        circ=circ, mcap=(circ * p_now) if circ else None, net_mcap=(tot_net / (circ * p_now) * 100) if circ and p_now else None,
        cum=[round(v, 2) for v in cum], ratio_series=[(d, v) for d, v in ratio], usd_series=[(d, p) for d, p in pxd],
        day_amt=[round(v, 2) for v in amt1],
    ))
json.dump(dict(start=START, end=END, hist=HIST, btc=sorted(btc.items()), coins=out), open(os.path.join(OUT, 'quiet.json'), 'w'))
print('coins', len(out))

U = [r for r in out if not r['excluded'] and r['listed_1y'] and r['have_days'] >= 360 and r['amt'] >= 3e7]
print('universe (비제외·상장 1년 이상·1년 거래대금 3천만 USDT 이상):', len(U))
def q(xs, p):
    xs = sorted(x for x in xs if x is not None)
    return xs[int(p * (len(xs) - 1))] if xs else None
for k in ['imb', 'pos_weeks', 'r2', 'top5_share', 'mdd_share', 'ratio_chg', 'ratio_pos_1y', 'ratio_pos_2y', 'ratio_vol90', 'ratio_range90', 'corr_btc', 'beta_btc', 'px_chg', 'vol30_vs_med']:
    print(f"  {k:14} p10 {q([r[k] for r in U],.1):9.3f} p25 {q([r[k] for r in U],.25):9.3f} p50 {q([r[k] for r in U],.5):9.3f} p75 {q([r[k] for r in U],.75):9.3f} p90 {q([r[k] for r in U],.9):9.3f}")
print('net>0:', sum(1 for r in U if r['net'] > 0), ' net>0 & net6>0:', sum(1 for r in U if r['net'] > 0 and r['net6'] > 0))
