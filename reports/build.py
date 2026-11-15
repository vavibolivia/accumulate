"""페이지 만들기: template.html + page_data.json + 문구 → <out>.html
사용: python build.py <an_dir> <page: quiet|lead>
"""
import json, sys, os, datetime
A = sys.argv[1]; PAGE = sys.argv[2]
DAY = 86400000
U = {r['base']: r for r in json.load(open(os.path.join(A, 'universe.json')))}
M = json.load(open(os.path.join(A, 'market.json')))
def ds(v): return datetime.datetime.fromtimestamp(v / 1000, datetime.UTC).strftime('%Y-%m-%d')
END = max(d for d, _ in U['BCH']['ratio_series']); START = END - 364 * DAY

def coin(b):
    r = U[b]; rs = [(d, v) for d, v in r['ratio_series'] if d >= START]; base = rs[0][1]
    if b == 'MORPHO' and not r.get('net_mcap'):
        r['net_mcap'] = r['net'] / 1.35e9 * 100   # 서버에 유통량이 없어 CoinGecko 시가총액 약 13.5억 달러(2026-11-14 보도값) 사용
    return dict(dates=[ds(d) for d, _ in rs], ratio=[round(v / base * 100, 2) for _, v in rs], cum=[round(v / 1e6, 3) for v in r['cum']],
                net=r['net'], net6=r['net6'], net90=r['net90'], pos_weeks=r['pos_weeks'], pos_months=r['pos_months'], n_months=r['n_months'],
                top5=r['top5_share'], px_chg=r['px_chg'], ratio_chg=r['ratio_chg'], tr120=r['tr120'], v30=r['vol30_vs_med'],
                net_mcap=r.get('net_mcap'), corr=r['corr_btc'], imb=r['imb'])

SRC = {
 'hack': ('바이낸스 해킹과 도난 알트코인 재매수 (2026-10-29~)', 'https://crypto.news/binance-buys-back-stolen-altcoins-after-hack/'),
 'hack2': ('해킹 경과 요약', 'https://www.trmlabs.com/resources/blog/north-korea-steals-usd-6-36-billion-from-binance-in-the-largest-hack-in-history'),
 'feb': ('2월 5~6일 BTC 6만 달러 급락', 'https://www.coindesk.com/markets/2026/02/06/bitcoin-s-crash-to-usd60-000-has-traders-hunting-for-a-hidden-fund-blowup'),
 'jul': ('7월 1일 BTC 57.7K 저점과 ETF 유출', 'https://www.tradingpedia.com/2026/07/01/bitcoin-slides-toward-58k-as-etf-outflows-rise/'),
 'fed': ('9월 16일 연준 금리 인상', 'https://www.cnbc.com/2026/09/16/fed-rate-decision-september-2026.html'),
 'clarity': ('9월 15일 CLARITY 법안 상원 표결 부결', 'https://www.americanbanker.com/news/crypto-market-structure-bill-fails-in-senate-vote-49-50'),
 'dom': ('BTC 점유율 59~60%', 'https://247wallst.com/investing/cryptocurrency/2026/10/28/bitcoin-controls-59-5-of-the-crypto-market-is-altcoin-season-still-possible/'),
 'comp': ('Compound 재단의 v2 준비금 COMP 매수 (온체인 추적)', 'https://bitquery.io/investigations/compound-dao-dai-reserves-vote'),
 'comp2': ('Compound 재단 입장문', 'https://www.comp.xyz/t/statement-on-the-use-of-v2-reserves/8108'),
 'bch': ('그레이스케일 BCH 신탁 ETF 전환 신청', 'https://coinmarketcap.com/top-stories/6aabbd8eac7e975cd333d763/'),
 'bchcme': ('CME BCH 선물', 'https://www.ad-hoc-news.de/boerse/news/marktberichte/bitcoin-cash-price-context-cme-s-october-20-futures-launch-adds-a-regulated-venue/70509154'),
 'floki': ('FLOKI 거래 봇 해킹 (11-05)', 'https://www.cryptotimes.io/2026/11/06/floki-suspends-trading-bot-after-security-exploit-affecting-wallets/'),
 'wct': ('WCT 토큰 해제 일정', 'https://dropstab.com/coins/walletconnect/vesting'),
 'newt': ('NEWT 배분·베스팅', 'https://docs.newton.xyz/protocol/token/distribution-and-vesting'),
 'ctsi': ('CTSI 공급 고정 논의', 'https://coinmarketcap.com/cmc-ai/cartesi/latest-updates/'),
 'jst': ('JST 5차 바이백·소각', 'https://cryptoslate.com/just-completes-5th-jst-buyback-burn-over-170m-tokens-23-53m-burned-ahead-of-major-deflationary-upgrade/'),
 'trxusdt': ('트론의 USDT 발행량이 이더리움 추월', 'https://crypto.news/tron-q3-2026-usdt-ethereum-stablecoin-supply/'),
 'trxhack': ('해킹 공격자의 TRX 교환·매도', 'https://cryptoticker.io/en/tron-trx-price-below-pre-hack-level-binance-attacker/'),
 'trxetf': ('Canary 스테이킹 TRX ETF 신청서', 'https://www.sec.gov/Archives/edgar/data/2064768/000199937126018236/trx-s1a_081926.htm'),
 'solflow': ('SOL 시장가 매수/매도 비율 보도', 'https://www.fxstreet.com/cryptocurrencies/news/solana-price-forecast-bearish-grip-tightens-on-sol-amid-broader-market-sell-off-202611130559'),
}
COMMON_VERIFIED = [
 '<b>수집 완료.</b> 바이낸스에서 거래 중인 현물 1,372개 페어의 1분봉을 상장한 달부터 모두 받았습니다(46,416개월 치).',
 '<b>바이낸스 원본과 일치.</b> 6개 코인 30개 페어의 365일 일별 순매수를 바이낸스 일봉의 시장가 매수 대금과 대조했고 10,950일 중 불일치는 0건입니다.',
 '<b>거래 연속성.</b> 10월 29일 해킹 보도 구간에도 BTCUSDT 1분봉은 하루 1,440개씩 빠짐없이 있습니다.',
 '<b>환율.</b> USDT가 아닌 호가(BTC·TRY·USDC 등) 페어는 그날의 종가 환율로 USDT로 바꿔 더했습니다.',
]
COMMON_LIMITS = [
 '<b>시장가 순매수는 보유량 증가가 아닙니다.</b> 급하게 산 쪽이 더 많았다는 뜻입니다. 지정가로 천천히 사 모으는 매집은 잡히지 않습니다.',
 '<b>바이낸스 현물만 봅니다.</b> 다른 거래소, 선물, 온체인 이동은 들어 있지 않습니다.',
 '<b>빠진 페어.</b> 한자 이름 코인 2개(6개 페어)는 서버 오류로 집계에 없습니다.',
 '<b>외부 자료.</b> 뉴스·토큰 일정은 검색으로 찾은 보도이고, 일부는 1차 자료를 직접 열어 보지 못했습니다.',
 '<b>투자 권유가 아닙니다.</b> 데이터가 보여 주는 것과 보여 주지 못하는 것을 구분해 읽어 주세요.',
]
MKT_TAKE = [
 '<b>BTC가 순매수였던 달은 13개월 중 3번</b>(2026년 4월, 8월, 11월 1~13일)뿐입니다. 그 외 코인 합계는 한 달도 순매수가 아니었습니다.',
 '<b>순매도가 가장 컸던 달은 2월과 6월</b>입니다(BTC −34.3억, −16.9억 USDT). 2월 5~6일 6만 달러 급락, 6~7월 ETF 자금 유출과 57.7K 저점 보도와 시기가 맞습니다.',
 '<b>순매수였던 코인은 498개 중 120개</b>(24%)입니다. 월별로는 18~45% 사이였습니다.',
 '<b>신흥국 통화로 USDT를 사는 흐름은 +17.7억 USDT</b>로 이어졌습니다(리라·헤알·페소 페어의 USDT 순매수). 코인으로 가지 않고 달러 대용으로 머문 돈입니다.',
 '<b>9월 반등은 짧았습니다.</b> 보도에 따르면 9월 15일 CLARITY 법안이 상원에서 부결됐고, 16일 연준이 금리를 올렸습니다. BTC 점유율은 59~60%로 올랐습니다.',
 '<b>10월 30일~11월 3일은 주의 구간</b>입니다. 바이낸스가 도난당한 알트코인을 거래소 안에서 되샀다고 밝혀 순매수가 부풀 수 있습니다.',
]
MKT_LEDE = '1년 동안 BTC는 94,594에서 68,314 USDT로 28% 내렸고, 코인 가격의 중앙값은 61% 내렸습니다. 시장가 체결은 BTC −118.5억, ETH −54.1억, 그 외 코인 −114.8억 USDT로 모두 순매도였습니다.'
SCOPE = ['<b>기간</b> 2025-11-14 ~ 2026-11-13 (365일, UTC)', '<b>데이터</b> 바이낸스 현물 1분봉 · 1,366개 페어', '<b>지표</b> 시장가 매수 − 시장가 매도 (그날 환율로 USDT 환산)', '<b>대상</b> 368개 코인 (스테이블·래핑·BTC 제외, 상장 1년 이상)']

if PAGE == 'quiet':
    out_name = 'quiet-accumulation.html'
    coins = ['BCH', 'COMP', 'FLOKI', 'CTSI', 'NEWT', 'WCT', 'TRX', 'JST', 'SOL']
    N = dict(
      title='조용한 매집 3선', eyebrow='바이낸스 현물 · 최근 1년 시장가 체결 분석', h1='하락장 바닥에서 조용히 사 모인 코인 3개',
      lede='비트코인으로 나눈 가격이 1년 바닥권에 머무는 동안, 시장가 순매수가 며칠에 몰리지 않고 꾸준히 쌓인 코인을 찾았습니다. 368개 코인 중 세 조건을 모두 통과한 것은 BCH, COMP, FLOKI입니다.',
      scope=SCOPE, pickList=['BCH', 'COMP', 'FLOKI'],
      conclH='결론: BCH · COMP · FLOKI',
      conclP='세 코인 모두 BTC로 나눈 가격은 1년 범위의 바닥권인데, 시장가 순매수는 1년 내내 쌓였습니다. 위 그래프는 BTC 대비 가격(1년 전 = 100), 아래 그래프는 누적 순매수입니다.',
      picks=dict(
        BCH=dict(name='Bitcoin Cash', tag='규모가 가장 큼',
                 verdict='6월에 BTC 대비 가격이 급락한 뒤 5개월째 바닥을 다지는 동안에도 순매수가 이어졌습니다. 2024년부터 3년 연속 순매수입니다(+1.89억, +2.05억, 올해 +1.63억 USDT).',
                 risk='"서서히"와는 거리가 있습니다. 순매수의 57%가 닷새에 몰려 있고, USDT 페어만 보면 상위 10일이 1년 순매수의 96%입니다. 2025-11-28 하루에 6,740만 USDT가 들어왔는데(시장가 매수 비율 84.5%, 체결 한 건이 평소의 6배) 가격은 3%만 올랐습니다. 소수의 큰 매수자와 그 물량을 내준 큰 매도자가 있었다는 뜻입니다. 가격은 1년간 54% 내렸습니다.'),
        COMP=dict(name='Compound', tag='가장 꾸준함',
                  verdict='13개월 모두 순매수였고, BTC 대비 가격은 10개월째 좁은 범위에서 움직입니다. 2023~2025년 순매도에서 올해 순매수로 돌아섰습니다. 시가총액 대비 순매수가 11.9%로 셋 중 가장 큽니다.',
                  risk='4월 9일~5월 2일의 +302만 USDT(1년의 13%)는 Compound 재단이 약 800만 달러를 바이낸스로 보내 COMP를 산 시기와 겹칩니다(온체인 추적 보도). 이 구간을 빼도 +1,990만 USDT입니다. 바이백이나 수수료 배분은 없고, 거버넌스 표 대결이 매수 동기일 수 있습니다.'),
        FLOKI=dict(name='FLOKI', tag='조건을 바꿔도 항상 통과',
                   verdict='기준값을 어떻게 바꿔도 통과한 유일한 코인입니다. 52주 중 37주가 순매수였고, 가장 많이 산 닷새의 비중이 19%로 셋 중 가장 고릅니다. 거래대금은 평소의 35%로 줄어 가장 조용합니다.',
                   risk='BTC 대비 가격이 아직 바닥을 만들지 못했습니다(최근 30일에 저점 갱신). 10~11월은 소폭 순매도로 돌아섰고, 리라 페어에서는 1년간 880만 USDT 순매도입니다. 11월 5일 거래 봇 해킹이 있었고, 확인된 구조적 매수자는 없습니다.'),
      ),
      howP='규모가 큰 코인이나 이미 오른 코인을 찾는 것이 아닙니다. 비트코인에 끌려 움직인 부분을 뺀 가격이 바닥인 상태에서, 순매수가 조용히 쌓인 코인을 찾았습니다. 조건은 세 가지이고 모두 통과해야 합니다.',
      steps=[dict(t='분석 대상', n='368', u='개 코인', d='거래 중 1,372개 페어를 코인별로 합쳤습니다. 스테이블·래핑·BTC 제외, 상장 1년 이상, 1년 거래대금 3천만 USDT 이상.'),
             dict(t='① 꾸준한 순매수', n='9', u='개', d='1년·6개월·90일 모두 순매수, 52주 중 60% 이상이 순매수, 네 분기 모두 증가, 상위 닷새 비중 60% 이하.'),
             dict(t='② 저점 ③ 조용함', n='5', u='개', d='BTC 대비 가격이 1년 범위의 하위 25%, 최근 90일 고저 폭 1.5배 이내, 최근 거래대금이 평소 이하.'),
             dict(t='바닥 확인', n='3', u='개', d='BTC 대비 120일 추세가 −15% 이상. 계속 밀리는 WCT(−31%)와 NEWT(−18%)를 뺐습니다.')],
      rulesH='세 가지 조건',
      rules=['<b>① 꾸준히 쌓였는가.</b> 누적 순매수가 일직선에 가깝게 늘고(R² 0.85 이상), 며칠의 큰 매수에 기대지 않아야 합니다.',
             '<b>② BTC에 끌려간 게 아닌 저점인가.</b> 가격을 BTC로 나누면 비트코인을 따라 움직인 부분이 빠집니다. 그 가격이 1년 범위의 바닥권이고, USDT 가격도 1년 전보다 낮아야 합니다.',
             '<b>③ 아직 조용한가.</b> 최근 90일 동안 BTC 대비 가격이 크게 움직이지 않았고, 거래대금이 늘지 않았어야 합니다.',
             '알트코인 대부분이 이미 바닥권이라(368개 중 291개가 ② 통과) 저점만으로는 걸러지지 않습니다. <b>실제로 가른 것은 ①</b>입니다. 통과한 코인이 9개뿐입니다.'],
      barsH='조건을 바꿔도 남는가', barsP='기준값을 느슨하게·엄격하게 바꾼 4,374가지 조합에서 각 코인이 통과한 비율입니다.',
      sens=[['FLOKI', 100, '100%'], ['COMP', 66.7, '67%'], ['NEWT', 66.7, '67%'], ['CTSI', 33.3, '33%'], ['BCH', 29.6, '30%'], ['WCT', 22.2, '22%'], ['BNT', 14.8, '15%'], ['WOO', 11.1, '11%']],
      sensNote='BCH는 "상위 닷새 비중"을 50% 이하로 조이면 빠지고, COMP는 "저점"을 하위 20%로 조이면 빠집니다(현재 23%). 세 코인의 순서는 확신의 순서가 아니라 순매수 규모 순서입니다.',
      mktH='시장 배경: 1년 내내 파는 쪽이 급했다', mktLede=MKT_LEDE, mktTake=MKT_TAKE,
      othH='빠진 코인과 그 이유', othP='순매수 규모만 보면 더 큰 코인이 있습니다. 목표에 맞지 않아 뺀 코인과, 조건 하나가 모자란 관찰 대상을 함께 적습니다.',
      groups=[dict(title='목표와 맞지 않아 뺀 코인 (순매수는 큼)', rows=[
                ['TRX', '순매수는 가장 크지만 BTC 대비 가격이 1년 범위 중간(55%)이라 저점이 아닙니다. 최근 90일은 순매도입니다.'],
                ['JST', '52주 중 43주가 순매수로 가장 꾸준하지만 가격이 이미 4배가 됐습니다. 조용한 저점이 아닙니다.'],
                ['SOL', '최근 90일 유입(+3.46억)은 가장 크지만 1~2월 대량 순매도 뒤의 반전이라 고르지 않습니다(R² 0.53).']]),
              dict(title='조건 하나가 모자란 관찰 대상', rows=[
                ['CTSI', '2월부터 순매수가 시작돼 첫 분기가 순매도였습니다. BTC 대비 가격은 1년 내내 횡보했습니다. 시가총액이 2,300만 달러로 작습니다.'],
                ['NEWT', '세 조건은 통과했지만 BTC 대비 가격이 계속 밀리고, 내부자 물량이 매달 풀립니다(보도).'],
                ['WCT', 'BTC 대비 120일 추세가 −31%입니다. 매달 유통량의 약 3%가 새로 풀립니다(보도).']])],
      verified=COMMON_VERIFIED + ['<b>일회성 구간 점검.</b> 해킹 직후 닷새가 1년 순매수에서 차지하는 비중은 BCH +0.3%, COMP +1.6%, FLOKI −2.1%입니다.'],
      limits=['<b>가격이 내리는데 순매수가 +라는 것</b>은 그만큼의 지정가 매도가 받아냈다는 뜻이기도 합니다. 매집으로 읽을지 물량 소화로 읽을지는 이 데이터만으로 가를 수 없습니다.'] + COMMON_LIMITS + ['<b>FLOKI는 바이낸스 비중이 작습니다.</b> 전체 거래의 약 3.5%라는 보도가 있어, 바이낸스만 본 흐름이 전체를 대표하지 않을 수 있습니다.'],
      sources=[SRC[k] for k in ['hack', 'hack2', 'feb', 'jul', 'fed', 'clarity', 'dom', 'comp', 'comp2', 'bch', 'bchcme', 'floki', 'wct', 'newt', 'ctsi']],
      foot='분석 기준일 2026-11-14. 가격·순매수는 2026-11-13(UTC) 마감분까지입니다. 방식은 C(체결): 바이낸스가 1분봉마다 기록한 시장가 매수 체결량.',
    )
else:
    sys.path.insert(0, A)
    import lead_notes
    out_name = 'strong-accumulation.html'
    coins = ['JST', 'TRX', 'MORPHO', 'COMP', 'GTC', 'ORCA', 'BCH', 'SOL', 'ZEC']
    N = lead_notes.notes(SRC, SCOPE, MKT_LEDE, MKT_TAKE, COMMON_VERIFIED, COMMON_LIMITS)

data = dict(coins={b: coin(b) for b in coins}, market=M['months'], btc=M['btc'])
tpl = open(os.path.join(A, 'template.html'), encoding='utf-8').read()
html = tpl.replace('/*TITLE*/', N['title']).replace('/*DATA*/', json.dumps(data, ensure_ascii=False, separators=(',', ':'))).replace('/*NOTES*/', json.dumps(N, ensure_ascii=False))
open(os.path.join(A, out_name), 'w', encoding='utf-8').write(html)
print(out_name, len(html) // 1024, 'KB')
