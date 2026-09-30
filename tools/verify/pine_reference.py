"""Independent re-implementation of the Pine v4 script, written in Pine's whole-series style
(not the streaming style of the C# engine), used to cross-check RutaCryptoMirror.cs.

Usage: python3 pine_reference.py <dir with bars_input.csv, nt_bars.csv, nt_trades.csv>
"""
import csv
import math
import sys

NA = None
WARMUP = 200  # BarsRequiredToTrade in the NinjaTrader strategy

P = dict(rsiupper=71, rsilower=20, vollevel=-39, sl=2, tp=4, shortlen=5, longlen=14,
         rsilen=14, volmalen=10, atrlen=14, trendlen=10)


def na(x):
    return x is None


def sma(src, n):
    out = [NA] * len(src)
    for i in range(n - 1, len(src)):
        w = src[i - n + 1:i + 1]
        if any(na(v) for v in w):
            continue
        out[i] = sum(w) / n
    return out


def recursive_ma(src, n, alpha):
    """Pine ema()/rma(): na until n non-na values, seeded with their SMA, na inputs skipped."""
    out = [NA] * len(src)
    seed, prev = [], NA
    for i, v in enumerate(src):
        if na(v):
            out[i] = prev
            continue
        if na(prev):
            seed.append(v)
            if len(seed) == n:
                prev = sum(seed) / n
            out[i] = prev
            continue
        prev = alpha * v + (1 - alpha) * prev
        out[i] = prev
    return out


def ema(src, n):
    return recursive_ma(src, n, 2.0 / (n + 1))


def rma(src, n):
    return recursive_ma(src, n, 1.0 / n)


def change_parts(x):
    u, d = [NA] * len(x), [NA] * len(x)
    for i in range(1, len(x)):
        if na(x[i]) or na(x[i - 1]):
            continue
        ch = x[i] - x[i - 1]
        u[i] = max(ch, 0.0)
        d[i] = max(-ch, 0.0)
    return u, d


def rsi(x, n):
    u, d = change_parts(x)
    ru, rd = rma(u, n), rma(d, n)
    out = [NA] * len(x)
    for i in range(len(x)):
        if na(ru[i]) or na(rd[i]):
            continue
        out[i] = 100.0 if rd[i] == 0 else 0.0 if ru[i] == 0 else 100 - 100 / (1 + ru[i] / rd[i])
    return out


def atr(h, l, c, n):
    tr = [h[0] - l[0]] + [max(h[i] - l[i], abs(h[i] - c[i - 1]), abs(l[i] - c[i - 1])) for i in range(1, len(c))]
    return rma(tr, n)


def gt(a, b):
    return not na(a) and not na(b) and a > b


def lt(a, b):
    return not na(a) and not na(b) and a < b


def crossover(a, level, i):
    return i > 0 and gt(a[i], level) and not na(a[i - 1]) and a[i - 1] <= level


def crossunder(a, level, i):
    return i > 0 and lt(a[i], level) and not na(a[i - 1]) and a[i - 1] >= level


def run(bars):
    o = [b['open'] for b in bars]
    h = [b['high'] for b in bars]
    l = [b['low'] for b in bars]
    c = [b['close'] for b in bars]
    v = [b['volume'] for b in bars]
    n = len(bars)

    # heikinashi ticker, same timeframe
    hc = [(o[i] + h[i] + l[i] + c[i]) / 4 for i in range(n)]
    ho = [NA] * n
    for i in range(n):
        ho[i] = (o[i] + c[i]) / 2 if i == 0 else (ho[i - 1] + hc[i - 1]) / 2

    s, lg = ema(v, P['shortlen']), ema(v, P['longlen'])
    osc = [NA if na(s[i]) or na(lg[i]) or lg[i] == 0 else 100 * (s[i] - lg[i]) / lg[i] for i in range(n)]
    src = sma(v, P['volmalen'])  # "Vol.: Volume MA"
    r = rsi(src, P['rsilen'])
    a = atr(h, l, c, P['atrlen'])
    trend = sma(c, P['trendlen'])

    longcond = [gt(hc[i], ho[i]) and crossover(r, P['rsilower'], i) and gt(osc[i], P['vollevel']) and gt(c[i], trend[i]) for i in range(n)]
    shortcond = [lt(hc[i], ho[i]) and crossunder(r, P['rsiupper'], i) and gt(osc[i], P['vollevel']) and lt(c[i], trend[i]) for i in range(n)]

    # --- script execution + broker emulator (process_orders_on_close=true) ---
    position = 0          # signed contracts
    entry_bar = entry_px = None
    tp = sl = NA
    trades, pos_before = [], []
    for i in range(n):
        lc = longcond[i] and i >= WARMUP
        sc = shortcond[i] and i >= WARMUP
        ps = position                      # strategy.position_size seen by the script
        pos_before.append((ps > 0) - (ps < 0))
        orders = []                        # placed in script order
        if lc and ps <= 0:
            sl, tp = c[i] - a[i] * P['sl'], c[i] + a[i] * P['tp']
            orders.append(('entry', 'Long', 1))
        if sc and ps >= 0:
            sl, tp = c[i] + a[i] * P['sl'], c[i] - a[i] * P['tp']
            orders.append(('entry', 'Short', -1))
        close_comment = None
        if (ps > 0 and c[i] >= tp) or (ps < 0 and c[i] <= tp):
            close_comment = 'Take Profit'
        if (ps > 0 and c[i] <= sl) or (ps < 0 and c[i] >= sl):
            close_comment = 'Stop Loss'    # same order id as the first close_all -> replaces its comment
        if close_comment:
            orders.append(('close_all', close_comment, -1 if ps > 0 else 1))  # side fixed at placement

        # fill at close, in placement order
        for kind, name, side in orders:
            if kind == 'entry':
                if position != 0 and (position > 0) != (side > 0):
                    trades.append((entry_bar, 'Long' if position > 0 else 'Short', entry_px, i, name, c[i]))
                    position = 0
                if position == 0:
                    position = side        # 1 contract
                    entry_bar, entry_px = i, c[i]
            else:
                # a close order can only reduce the position it was placed against; if that position
                # was reversed by an earlier order of this bar, it is cancelled
                if position != 0 and (position > 0) == (side < 0):
                    trades.append((entry_bar, 'Long' if position > 0 else 'Short', entry_px, i, name, c[i]))
                    position = 0

    return dict(ho=ho, hc=hc, src=src, rsi=r, osc=osc, atr=a, trend=trend,
                longcond=longcond, shortcond=shortcond, pos=pos_before, trades=trades)


def read_csv(path):
    with open(path, newline='') as f:
        return list(csv.DictReader(f))


def main(d):
    raw = read_csv(d + '/bars_input.csv')
    bars = [{k: float(r[k]) for k in ('open', 'high', 'low', 'close', 'volume')} for r in raw]
    times = [r['time'][:16] for r in raw]
    ref = run(bars)

    nt = read_csv(d + '/nt_bars.csv')
    cols = [('ha_open', 'ho'), ('ha_close', 'hc'), ('rsi_source', 'src'), ('rsi', 'rsi'),
            ('vol_osc', 'osc'), ('atr', 'atr'), ('sma_trend', 'trend')]
    bad = 0
    worst = {}
    for i, row in enumerate(nt):
        for ncol, rcol in cols:
            a = row[ncol]
            b = ref[rcol][i]
            if a == '' and b is None:
                continue
            if (a == '') != (b is None):
                bad += 1
                if bad < 10:
                    print('na mismatch bar', i, ncol, a, b)
                continue
            err = abs(float(a) - b) / max(1.0, abs(b))
            worst[ncol] = max(worst.get(ncol, 0.0), err)
            if err > 1e-6:
                bad += 1
                if bad < 10:
                    print('value mismatch bar', i, ncol, a, b)
        for ncol, rcol in (('longcond', 'longcond'), ('shortcond', 'shortcond')):
            if (row[ncol] == '1') != ref[rcol][i]:
                bad += 1
                if bad < 10:
                    print('condition mismatch bar', i, ncol)
        if int(row['position_sign']) != ref['pos'][i]:
            bad += 1
            if bad < 10:
                print('position mismatch bar', i, row['position_sign'], ref['pos'][i])
    print('per-bar values: %d bars, %d mismatches, max rel. error %s' % (len(nt), bad, {k: '%.1e' % v for k, v in worst.items()}))

    ntt = read_csv(d + '/nt_trades.csv')
    nt_trades = []
    for k in range(0, len(ntt), 2):
        e, x = ntt[k], ntt[k + 1]
        nt_trades.append((e['NT bar time (close)'], e['Type'].split()[1], float(e['Price']),
                          x['NT bar time (close)'], x['Signal'], float(x['Price'])))
    # bars_input.csv holds NinjaTrader bar (close) timestamps
    ref_trades = [(times[eb], side, ep, times[xb], sig, xp) for (eb, side, ep, xb, sig, xp) in ref['trades']]
    same = nt_trades == ref_trades
    print('trades: C# %d, Python %d, identical: %s' % (len(nt_trades), len(ref_trades), same))
    if not same:
        for a, b in zip(nt_trades, ref_trades):
            if a != b:
                print('first trade diff:\n  C#     ', a, '\n  Python ', b)
                break
    ok = bad == 0 and same
    print('ALL PYTHON CROSS-CHECKS PASSED' if ok else 'PYTHON CROSS-CHECK FAILED')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else '.'))
