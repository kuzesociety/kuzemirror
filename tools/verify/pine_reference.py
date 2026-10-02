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


def usd_to_points(usd, qty, pv, tick):
    pts = usd / (max(1, qty) * pv)
    return max(tick, round(pts / tick) * tick)   # round() is round-half-even, like C# Math.Round


def intrabar(d, stop, target, o, h, l):
    """Working stop + target orders during one bar: (-1 stop | 1 target | 0, fill). Gap -> open; both touched -> stop."""
    if d > 0:
        if o <= stop: return -1, o
        if o >= target: return 1, o
        if l <= stop: return -1, stop
        if h >= target: return 1, target
    else:
        if o >= stop: return -1, o
        if o <= target: return 1, o
        if h >= stop: return -1, stop
        if l <= target: return 1, target
    return 0, None


def run(bars, use_ha=True, use_vol=True, use_sma=True, use_pd=False, pd_level=100.0, units='atr', execution='close',
        sl_usd=1000.0, tp_usd=1000.0, qty=1, pv=20.0, tick=0.25, max_qty=10):
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

    # filter results per side (a switched-off filter always passes)
    ha_ok = {1: [not use_ha or gt(hc[i], ho[i]) for i in range(n)], -1: [not use_ha or lt(hc[i], ho[i]) for i in range(n)]}
    vol_ok = [not use_vol or gt(osc[i], P['vollevel']) for i in range(n)]
    sma_ok = {1: [not use_sma or gt(c[i], trend[i]) for i in range(n)], -1: [not use_sma or lt(c[i], trend[i]) for i in range(n)]}
    trig = {1: [crossover(r, P['rsilower'], i) for i in range(n)], -1: [crossunder(r, P['rsiupper'], i) for i in range(n)]}
    # previous session high / low; a session counts only if it began at a session start
    new_session = [b.get('new_session', False) for b in bars]
    pdh, pdl = [NA] * n, [NA] * n
    cur_hi = cur_lo = ph = pl = NA
    complete = False
    for i in range(n):
        if new_session[i]:
            if complete:
                ph, pl = cur_hi, cur_lo
            complete = True
            cur_hi, cur_lo = h[i], l[i]
        else:
            cur_hi = h[i] if na(cur_hi) else max(cur_hi, h[i])
            cur_lo = l[i] if na(cur_lo) else min(cur_lo, l[i])
        pdh[i], pdl[i] = ph, pl
    # range level: longs above PDL + level x range, shorts below PDH - level x range (100 = PDH / PDL)
    pd_long = [NA if na(pdh[i]) else pdl[i] + pd_level / 100.0 * (pdh[i] - pdl[i]) for i in range(n)]
    pd_short = [NA if na(pdh[i]) else pdh[i] - pd_level / 100.0 * (pdh[i] - pdl[i]) for i in range(n)]
    pd_ok = {1: [not use_pd or gt(c[i], pd_long[i]) for i in range(n)], -1: [not use_pd or lt(c[i], pd_short[i]) for i in range(n)]}

    longcond = [ha_ok[1][i] and trig[1][i] and vol_ok[i] and sma_ok[1][i] and pd_ok[1][i] for i in range(n)]
    shortcond = [ha_ok[-1][i] and trig[-1][i] and vol_ok[i] and sma_ok[-1][i] and pd_ok[-1][i] for i in range(n)]

    def contracts(i):
        if units != 'usd_atr':
            return qty
        if na(a[i]) or not a[i] * P['sl'] * pv > 0:
            return 1
        q = math.floor(sl_usd / (a[i] * P['sl'] * pv))     # contracts for a ~sl ATR stop
        return max_qty if q > max_qty else 1 if q < 1 else int(q)

    def dist(i, which):
        if units in ('usd', 'usd_atr'):
            return usd_to_points(sl_usd if which == 'sl' else tp_usd, contracts(i), pv, tick)
        return a[i] * P[which]

    # --- script execution + broker emulator (process_orders_on_close=true) ---
    position = 0          # signed contracts
    entry_bar = entry_px = None
    tp = sl = NA
    trades, pos_before, entry_dir = [], [], [0] * n
    pos_qty = 0
    for i in range(n):
        if execution == 'orders' and position != 0:
            res, fill = intrabar(position, sl, tp, o[i], h[i], l[i])
            if res:
                trades.append((entry_bar, 'Long' if position > 0 else 'Short', entry_px, i,
                               'Stop Loss' if res < 0 else 'Take Profit', fill, pos_qty))
                position = 0
        lc = longcond[i] and i >= WARMUP
        sc = shortcond[i] and i >= WARMUP
        ps = position                      # strategy.position_size seen by the script
        pos_before.append((ps > 0) - (ps < 0))
        orders = []                        # placed in script order
        if lc and ps <= 0:
            sl, tp = c[i] - dist(i, 'sl'), c[i] + dist(i, 'tp')
            orders.append(('entry', 'Long', 1))
        if sc and ps >= 0:
            sl, tp = c[i] + dist(i, 'sl'), c[i] - dist(i, 'tp')
            orders.append(('entry', 'Short', -1))
        close_comment = None
        if execution != 'close':
            pass
        elif (ps > 0 and c[i] >= tp) or (ps < 0 and c[i] <= tp):
            close_comment = 'Take Profit'
        if execution == 'close' and ((ps > 0 and c[i] <= sl) or (ps < 0 and c[i] >= sl)):
            close_comment = 'Stop Loss'    # same order id as the first close_all -> replaces its comment
        if close_comment:
            orders.append(('close_all', close_comment, -1 if ps > 0 else 1))  # side fixed at placement

        # fill at close, in placement order
        for kind, name, side in orders:
            if kind == 'entry':
                if position != 0 and (position > 0) != (side > 0):
                    trades.append((entry_bar, 'Long' if position > 0 else 'Short', entry_px, i, name, c[i], pos_qty))
                    position = 0
                if position == 0:
                    position = side        # direction; size in pos_qty
                    pos_qty = contracts(i)
                    entry_bar, entry_px = i, c[i]
                    entry_dir[i] = side
            else:
                # a close order can only reduce the position it was placed against; if that position
                # was reversed by an earlier order of this bar, it is cancelled
                if position != 0 and (position > 0) == (side < 0):
                    trades.append((entry_bar, 'Long' if position > 0 else 'Short', entry_px, i, name, c[i], pos_qty))
                    position = 0

    # --- setup report: every bar/side followed forward with the SL/TP-on-close rule ---
    buckets = {(b, sd): [0, 0, 0, 0.0] for b in range(10) for sd in ('long', 'short')}
    setups = []
    for i in range(WARMUP, n):
        if na(a[i]) or not a[i] > 0:
            continue
        for d in (1, -1):
            entry, risk = c[i], dist(i, 'sl')
            stop, target = c[i] - d * dist(i, 'sl'), c[i] + d * dist(i, 'tp')
            outcome, res_bar, fill = 0, None, None
            for j in range(i + 1, n):
                if execution == 'orders':
                    res, f = intrabar(d, stop, target, o[j], h[j], l[j])
                else:
                    hit_t = c[j] >= target if d > 0 else c[j] <= target
                    hit_s = c[j] <= stop if d > 0 else c[j] >= stop
                    res, f = (-1 if hit_s else 1 if hit_t else 0), c[j]
                if res:
                    outcome, res_bar, fill = res, j, f
                    break
            h_ok, v_ok, s_ok, pdk = ha_ok[d][i], vol_ok[i], sma_ok[d][i], pd_ok[d][i]
            p_ok = pos_before[i] <= 0 if d > 0 else pos_before[i] >= 0
            groups = {0}
            if trig[d][i]:
                groups.add(1)
                if h_ok: groups.add(2)
                if h_ok and v_ok: groups.add(3)
                if h_ok and v_ok and s_ok and pdk: groups.add(4)
                if pd_level == 100:
                    pd_name = 'PDH' if d > 0 else 'PDL'
                else:
                    pd_name = 'PD' + ('%.2f' % pd_level).rstrip('0').rstrip('.') + '%'
                failed = [name for name, ok in (('HA', h_ok), ('VOL', v_ok), ('SMA', s_ok), (pd_name, pdk)) if not ok]
                if len(failed) == 1:
                    groups.add({'HA': 5, 'VOL': 6, 'SMA': 7, pd_name: 8}[failed[0]])
                if not failed and not p_ok:
                    groups.add(9)
                blockers = ' '.join(failed) if failed else ('' if p_ok else 'in position')
                rr = (fill - entry) * d / risk if res_bar is not None else None
                setups.append((i, 'long' if d > 0 else 'short', int(h_ok), int(v_ok), int(s_ok), int(pdk), int(p_ok),
                               int(entry_dir[i] == d), blockers, {1: 'win', -1: 'loss', 0: 'open'}[outcome], res_bar,
                               None if rr is None else round(rr, 2)))
            if res_bar is not None:
                rr = (fill - entry) * d / risk
                for g in groups:
                    k = buckets[(g, 'long' if d > 0 else 'short')]
                    k[0] += 1
                    k[1 if outcome > 0 else 2] += 1
                    k[3] += rr

    return dict(ho=ho, hc=hc, src=src, rsi=r, osc=osc, atr=a, trend=trend,
                longcond=longcond, shortcond=shortcond, pos=pos_before, trades=trades, pdh=pdh, pdl=pdl,
                pd_long=pd_long, pd_short=pd_short,
                setups=setups, buckets=buckets)


def read_csv(path):
    with open(path, newline='') as f:
        return list(csv.DictReader(f))


def load_trades(path):
    rows = read_csv(path)
    return [(rows[k]['NT bar time (close)'], rows[k]['Type'].split()[1], float(rows[k]['Price']),
             rows[k + 1]['NT bar time (close)'], rows[k + 1]['Signal'], float(rows[k + 1]['Price']), int(rows[k + 1]['Contracts']))
            for k in range(0, len(rows), 2)]


def ref_trade_rows(ref, times):
    return [(times[eb], side, ep, times[xb], sig, xp, q) for (eb, side, ep, xb, sig, xp, q) in ref['trades']]


def load_setups(path):
    return [(r['nt_bar_time'], r['side'], int(r['ha_ok']), int(r['vol_ok']), int(r['sma_ok']), int(r['pd_ok']), int(r['pos_ok']),
             int(r['taken']), r['blocked_by'], r['outcome'], r['resolved_nt_time'],
             None if r['r_multiple'] == '' else float(r['r_multiple'])) for r in read_csv(path)]


def ref_setup_rows(ref, times):
    return [(times[i], sd, h_, v_, s_, pd_, p_, t_, bl, oc, '' if rb is None else times[rb], rr)
            for (i, sd, h_, v_, s_, pd_, p_, t_, bl, oc, rb, rr) in ref['setups']]


def buckets_match(path, ref_buckets):
    bk = {}
    for line in open(path):
        b, side, nn, w, l, sr = line.strip().split(',')
        bk[(int(b), side)] = (int(nn), int(w), int(l), float(sr))
    return all(bk[key][:3] == tuple(v[:3]) and abs(bk[key][3] - v[3]) <= 1e-9 * max(1.0, abs(v[3]))
               for key, v in ref_buckets.items())


def main(d):
    raw = read_csv(d + '/bars_input.csv')
    bars = [{k: float(r[k]) for k in ('open', 'high', 'low', 'close', 'volume')} for r in raw]
    for b, r in zip(bars, raw):
        b['new_session'] = r.get('new_session') == '1'
    times = [r['time'][:16] for r in raw]
    ref = run(bars, pd_level=50.0)     # the strategy's default level (filter off)

    nt = read_csv(d + '/nt_bars.csv')
    cols = [('ha_open', 'ho'), ('ha_close', 'hc'), ('rsi_source', 'src'), ('rsi', 'rsi'),
            ('vol_osc', 'osc'), ('atr', 'atr'), ('sma_trend', 'trend'), ('pdh', 'pdh'), ('pdl', 'pdl'),
            ('pd_long_level', 'pd_long'), ('pd_short_level', 'pd_short')]
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

    nt_trades = load_trades(d + '/nt_trades.csv')
    # bars_input.csv holds NinjaTrader bar (close) timestamps
    ref_trades = ref_trade_rows(ref, times)
    same = nt_trades == ref_trades
    print('trades: C# %d, Python %d, identical: %s' % (len(nt_trades), len(ref_trades), same))
    if not same:
        for a, b in zip(nt_trades, ref_trades):
            if a != b:
                print('first trade diff:\n  C#     ', a, '\n  Python ', b)
                break
    # setup report
    bk = {}
    for line in open(d + '/nt_buckets.csv'):
        b, side, nn, w, l, sr = line.strip().split(',')
        bk[(int(b), side)] = (int(nn), int(w), int(l), float(sr))
    bucket_ok = all(bk[key][:3] == tuple(v[:3]) and abs(bk[key][3] - v[3]) <= 1e-9 * max(1.0, abs(v[3]))
                    for key, v in ref['buckets'].items())
    print('setup report buckets identical: %s (baseline long n=%d)' % (bucket_ok, ref['buckets'][(0, 'long')][0]))

    nt_setups = load_setups(d + '/nt_setups.csv')
    ref_setups = ref_setup_rows(ref, times)
    setups_ok = nt_setups == ref_setups
    print('skipped/taken RSI setups: C# %d, Python %d, identical: %s' % (len(nt_setups), len(ref_setups), setups_ok))
    if not setups_ok:
        for x, y in zip(nt_setups, ref_setups):
            if x != y:
                print('first setup diff:\n  C#     ', x, '\n  Python ', y)
                break

    # filter switch: SMA trend filter off
    ref2 = run(bars, use_sma=False)
    nt2 = load_trades(d + '/nt_trades_nosma.csv')
    rt2 = ref_trade_rows(ref2, times)
    switch_ok = nt2 == rt2
    print('SMA filter off: C# %d trades, Python %d, identical: %s' % (len(nt2), len(rt2), switch_ok))

    modes_ok = True
    for suffix, kw in (('usd_close', dict(units='usd', sl_usd=500.0, tp_usd=1000.0)),
                       ('usd_orders', dict(units='usd', sl_usd=500.0, tp_usd=1000.0, execution='orders')),
                       ('usd_atr', dict(units='usd_atr', sl_usd=1000.0, tp_usd=1500.0, execution='orders', pv=2.0, max_qty=12)),
                       ('pd', dict(use_pd=True, pd_level=50.0)),
                       ('pd100', dict(use_pd=True, pd_level=100.0))):
        refm = run(bars, **kw)
        t_ok = load_trades(d + '/nt_trades_%s.csv' % suffix) == ref_trade_rows(refm, times)
        s_ok = load_setups(d + '/nt_setups_%s.csv' % suffix) == ref_setup_rows(refm, times)
        b_ok = buckets_match(d + '/nt_buckets_%s.csv' % suffix, refm['buckets'])
        print('%s: %d trades; trades identical %s, setups identical %s, report identical %s' % (suffix, len(refm['trades']), t_ok, s_ok, b_ok))
        modes_ok = modes_ok and t_ok and s_ok and b_ok

    ok = bad == 0 and same and bucket_ok and setups_ok and switch_ok and modes_ok
    print('ALL PYTHON CROSS-CHECKS PASSED' if ok else 'PYTHON CROSS-CHECK FAILED')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else '.'))
