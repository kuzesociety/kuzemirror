# kuzemirror: the "rutacrypto" TradingView strategy, mirrored in NinjaTrader 8

| File | What it is |
|---|---|
| `ninjatrader/Strategies/RutaCryptoMirror.cs` | The NinjaTrader 8 strategy. It trades the Pine logic bar for bar and draws the TradingView-style trades on the chart. |
| `tradingview/rutacrypto_original.pine` | Your Pine v4 script, unchanged, kept for reference. |
| `tradingview/rutacrypto_debug.pine` | The same script with identical trading logic. It adds hidden Data Window values so you can compare numbers bar by bar with NinjaTrader. |
| `tools/verify/` | The automated checks used to validate the port (see the end of this file). |

The martingale and leverage parts are removed. Nothing in the entry or exit logic depends on them; they only changed the order size.

---

## 1. Install in NinjaTrader 8

1. Copy `RutaCryptoMirror.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\`.
2. In NinjaTrader, open **New → NinjaScript Editor**, open any strategy and press **F5** to compile. The file uses only C# 5 syntax, which is what NinjaTrader 8's compiler accepts.
3. Open a chart and use **Strategies… → RutaCryptoMirror**. Tick the **Enabled** box, then **OK**. NinjaTrader adds strategies *disabled*, and a disabled strategy draws nothing. Once enabled, historical trades are drawn right away. A status box appears at the bottom-left of the chart.

**Side-by-side copies:** each version installs next to the others under its own name, because every name inside is changed (strategy, dropdown types, drawing tags, CSV folder). They are generated with `tools/make_variant.sh <suffix>`. Settings templates are saved per strategy name, so re-enter your values in each one.
* `RutaCryptoMirrorV2.cs`: frozen snapshot with the dollar exits and ATR sizing. Don't regenerate it.
* `RutaCryptoMirrorV3.cs`: frozen. V2 plus the **Use PDH/PDL filter** switch (off = identical to V2).
* `RutaCryptoMirrorV4.cs`: frozen. V3 plus **PDH/PDL Range Level (%)** (default 50 = middle of the previous session's range; 100 = identical to V3's filter; filter off = identical to V2/V3).
* `RutaCryptoMirrorV5.cs`: frozen. V4 plus **9. Daily rules & trading hours** (all off = identical to V4).
* `RutaCryptoMirrorV6.cs`: V5 plus **10. Prop accounts (simulated rotation)** with the accounts dashboard (off = identical to V5).

**Nothing on the chart?**
* No status box at the bottom-left: the strategy is not running. Check the **Enabled** box. Then look at **Control Center → Log** for a red line mentioning RutaCryptoMirror.
* Status box but no trades: its last line says why: `NOT ENOUGH DATA` (load more days), `NO VOLUME` (this instrument's data has no real volume), the RSI never crossed its levels, or the other filters rejected every cross.
* **New → NinjaScript Output** shows the same diagnosis, plus the setup report (section 7), when loading finishes.

Chart setup for a fair comparison:

* **Same instrument and timeframe as the TradingView chart**, 5 minutes. See section 3 before comparing.
* **Trading hours:** `CME US Index Futures ETH`, the full 23-hour session like TradingView futures charts.
* **Days to load:** at least 10. The first 200 bars are warm-up (`Bars required to trade`) and never trade.
* **Contract rollover:** set Merge policy to *Merge non back-adjusted*, or use the single contract. On TradingView, use the same contract (for example `NQZ2026`) instead of `NQ1!`.
* **Your own fills:** Chart properties → *Plot executions* = *Text and marker* shows NinjaTrader's fills next to the TradingView ones.

## 2. Settings (the defaults already equal your screenshots)

| TradingView input | NinjaTrader property | Value |
|---|---|---|
| RSI Upper Level | RSI Upper Level | 71 |
| RSI Lower Level | RSI Lower Level | 20 |
| Volume Increase Threshold | Volume Increase Threshold | -39 |
| Stop Loss ATR Multiplier | Stop Loss ATR Multiplier | 2 |
| Take Profit ATR Multiplier | Take Profit ATR Multiplier | 4 |
| Longitud corta / larga | Short Length / Long Length | 5 / 14 |
| RSI Fuente = `Vol.: Volume MA` | RSI Source = `VolumeMA`, Volume MA Length = 10, Volume MA Type = SMA | |
| RSI Longitud | RSI Length | **14** (the code default is 12; your chart uses 14) |
| `atr(14)`, `sma(close, 10)` (hard-coded) | ATR Length / Trend SMA Length | 14 / 10 |
| Lookback Candles = 7 | *(not ported: the Pine code never uses it)* | |
| *(new, not in Pine)* | Stop/Target Units (AtrMultiple / Dollars / DollarsSizedByAtr), Stop Loss ($), Take Profit ($), Exit Execution | AtrMultiple, 1000, 1000, OnBarClose = TradingView. See section 9. |
| *(new, not in Pine)* | Use PDH/PDL filter, PDH/PDL Range Level (%) (group 8) | off = TradingView. On: longs only when the close is above the chosen level of the previous session's range, shorts only below it. |

**Check the Volume MA length.** The RSI source is the *Volume MA line of TradingView's Volume indicator*. The `Vol. 10` in the volume pane suggests MA length 10. Open that indicator's settings and put the same length in `Volume MA Length`. If the source dropdown actually says `Vol.: Volume` and not `Volume MA`, set RSI Source = `Volume`.

Keep these NinjaTrader settings as they are:

* `Calculate` = **On bar close**
* `Exit on session close` = **false**. NinjaTrader's template default is true, which flattens every night. TradingView never does that.

## 3. Read this first: the data has to be the same

Your TradingView chart is **`NAS100`, a CFD**. Its volume is the broker's *tick count*: about 150–750 per 5-minute bar in your screenshot. NQ/MNQ futures in NinjaTrader report *exchange contract volume*, which is a different series with different ups and downs.

Two of the four entry filters are pure volume: the **RSI of the Volume MA crossing 20/71** (the main trigger) and the volume oscillator. So the same, correct code produces different trades on NAS100-CFD volume than on NQ volume. That is probably the biggest reason every port "didn't trade like TradingView". No code can fix it.

To check the port apples to apples, put the same instrument on both sides:

* **TradingView:** `CME_MINI:NQZ2026` (or MNQ), 5 minutes, with this strategy.
* **NinjaTrader:** `NQ 12-26`, 5 minutes, with the trading hours above.

Even then, two data vendors rarely build exactly the same bar volumes (block trades, late prints, and so on). Expect nearly all trades to line up, with an occasional extra or missing one where the RSI sat right on 20 or 71.

## 4. What the port reproduces exactly (and what most ports get wrong)

1. **Fills at the signal bar's close.** `process_orders_on_close=true` fills every TradingView order at the *close of the bar that produced it*. A normal NinjaTrader strategy fills at the next bar's open.
2. **SL/TP are checked on the close only.** The Pine script has no stop or limit orders. It calls `close_all` when the *close* is beyond the level. Ports that use `SetStopLoss`/`SetProfitTarget` exit intrabar on wicks and produce completely different trades.
3. **Script order matters.** A new entry overwrites `stopLossLevel`/`profitTarget` *before* the exit block runs. The exit block then uses the *old* position size. The entry bar itself never exits.
4. **Reversals.** On a reversal bar both `close_all` conditions are always true, because the levels were just overwritten. TradingView still reverses in **one order** and drops those `close_all` calls. Your own chart shows it: `-47.2 Short` (19.3 long closed + 27.9 short opened), then `Take Profit +27.9`.
5. **Heikin Ashi is only a filter.** `security(heikinashi(...))` HA open/close are computed from normal candles. Orders fill at *real* prices. Do **not** run it on a NinjaTrader Heikin Ashi chart.
6. **Pine math, not NinjaTrader built-ins.** `ema`, `rma`, `rsi`, `atr` and `sma` are written the way Pine computes them, including SMA seeding and TradingView's RSI edge cases. NinjaTrader's built-ins start differently.
7. **RSI source and length:** the Volume MA and 14, from your settings, not `close` and 12 from the code.

## 5. What you see on the NinjaTrader chart

| On the chart | Meaning |
|---|---|
| Red / green line | Pine `plot(plotSL)` / `plot(plotTP)`, drawn on the same bars as TradingView |
| **Sell** / **Buy** box | The two `plotshape()` signals |
| `-1` / `Short`, `Take Profit` / `+1`, and a diamond | TradingView's order markers. The diamond is at the **TradingView fill price** (the bar close). Reversals show the full order size, like `-47.2` on TradingView. |
| Dotted green/red line | Entry → exit of each TradingView trade (win / loss) |
| Box at bottom left | Bars processed, trades, win rate, net points, profit factor and open position, all computed with TradingView's fill-at-close rule. The last line counts RSI crosses and Buy/Sell signals, and says which filter blocked trading when there are none. |
| NinjaTrader's own execution markers | The real NinjaTrader orders. They fill one tick later (in backtests: the next bar's open). Named `Long`/`Short`/`Take Profit`/`Stop Loss` like on TradingView; a reversal appears as NinjaTrader's automatic `Close position` plus the new entry. |

Turn **Enable Orders** off to use it as a pure "TradingView trades" indicator.

## 6. Bar-by-bar comparison (when a trade differs)

1. In NinjaTrader, turn on **Export Trades CSV** and/or **Export Bar Values CSV**. Files are written to `Documents\NinjaTrader 8\RutaCryptoMirror\` when the historical load finishes and when the strategy is disabled. Or turn on **Print Trades To Output** to see them in the Output window.
2. In TradingView, add `tradingview/rutacrypto_debug.pine` and set the same inputs again (RSI source = Volume MA, length 14…). Open the **Data Window** and hover the bar. The `dbg …` values are the same columns, in the same order, as the NinjaTrader bars CSV: `ha_open, ha_close, rsi_source, rsi, vol_osc, atr, sma_trend, longcond, shortcond, position_sign`.
3. **Time stamps:** TradingView labels a bar by its **open** time; NinjaTrader by its **close** time. Both CSVs have both columns (`tv_bar_time` / `Date/Time (TV bar open)`). Also check that both platforms use the same time zone.

If `rsi_source` (the Volume MA) differs, the volume data differs (section 3). If it matches but `rsi` differs early in the chart, load more days: RSI and ATR are recursive and need warm-up.

## 7. Why a good move wasn't traded, and how to optimize

A move gets missed in one of two ways. The chart and the Output window tell you which.

**a) The trigger never fired.** Every trade starts with the RSI of the Volume MA crossing up through 20 (long) or down through 71 (short). If a move has **no marker at all**, that cross didn't happen there. No filter setting changes that. Only RSI Lower/Upper Level, RSI Length, Volume MA Length or the RSI source do.

**b) The trigger fired but something blocked it.** Those bars get a small label (**Show Skipped Setups**):

| Label | Blocked by |
|---|---|
| `skip buy: HA` / `skip sell: HA` | Heikin Ashi candle had the wrong color |
| `… VOL` | Volume oscillator at or below the *Volume Increase Threshold* |
| `… SMA` | Close on the wrong side of the 10-bar SMA |
| `… in position` | Already in a trade in that direction; the Pine script doesn't add to positions |

Labels start **gray**. Once price reaches the stop or the target on a close, they turn **green "(won)"** or **red "(lost)"**. That is a hindsight check of the same trade with the same SL/TP rule.

### The setup report (NinjaScript Output, printed when the chart finishes loading)

```
SETUP REPORT (hindsight): every possible entry at the bar close, exited by the strategy's own rule ...
  Every bar, no filters (baseline)       | LONG  n= 5772  win  40.5%  avg R  +0.20 | SHORT ...
  RSI cross (trigger only)               | LONG  n=   52  win  48.1%  avg R  +0.46 | SHORT ...
  RSI cross + Heikin Ashi                | ...
  RSI cross + HA + Volume osc            | ...
  All filters pass = signals             | ...
  Blocked ONLY by Heikin Ashi            | ...
  Blocked ONLY by Volume osc             | ...
  Blocked ONLY by SMA trend              | ...
  Signal skipped: already in position    | ...
```
*(example numbers from test data, not from your market)*

*R* is the result in units of the stop distance: a target hit is about +2R with the default 2/4 ATR, a stop is about −1R. **avg R above 0 means that group of entries makes money**; below 0 means it loses. How to read it:

* **Baseline vs "RSI cross"**: if entering on the RSI cross is not clearly better than entering on any bar, the trigger has no edge on this instrument's volume data.
* **Each "+ filter" row** should raise avg R. If adding a filter doesn't raise it, that filter only removes trades.
* **"Blocked ONLY by X"** lists setups where X was the *only* filter that failed. If that row has positive avg R and a decent n, filter X is throwing away winners. Try turning it off (group *8. Filter switches*) or loosening its level.
* Rows with **n below ~30** are noise. Load more days before drawing conclusions.
* With **Export Trades CSV** on, `<instrument>_setups.csv` lists every trigger bar: its filters, whether it was taken, and its outcome. You can filter it in Excel.

The report looks into the future. It is for analysis only; the strategy never uses it to trade.

### Optimizing in NinjaTrader: the full plan

**Fixed frame for every pass** (Control Center → New → Strategy Analyzer):
* MNQ, 5 minutes, trading hours *CME US Index Futures ETH*. Tuning period: e.g. 6 months. Keep the 3 most recent months unseen.
* Include commission ✓, Slippage 1, Exit on session close ✗, Bars required to trade 200.
* Stop/Target Units = **DollarsSizedByAtr**, Exit Execution = **StopTargetOrders**, **Stop Loss $ = 1000** (fixed in every pass), Take Profit $ = 1500, Max Contracts = 40.
* Enable Orders = True; every *Show / Print / Export* option = False.
* Optimize on **Max profit factor**, Keep best # results = 50. For every parameter not being tested in a pass, set Min = Max.

In *DollarsSizedByAtr* the Stop Loss $ only sets the contract count; the stop distance comes from ATR. So $500 and $2000 give the same trades at a different size, and profit factor can't tell them apart. **Don't optimize Stop Loss $.** Pick it last from your account size (0.5–1% risk per trade). What changes the trades is the **Stop Loss ATR Multiplier** (stop width) and **Take Profit $ ÷ Stop Loss $** (target width).

| Pass | What | Parameters (Min → Max, step) | Runs |
|---|---|---|---|
| 0 | Baseline | nothing: Backtest type Standard. Write down PF, trades, max drawdown | 1 |
| 1 | Trigger | RSI Lower Level 10 → 40, 5 · RSI Upper Level 60 → 90, 5 | 49 |
| 2 | Filters | Use Heikin Ashi / Volume osc / SMA trend: True and False · Volume Increase Threshold −60 → 20, 10 | 72 |
| 3 | Exits | Take Profit $ 500 → 3000, 500 (= 0.5× to 3× the stop) · Stop Loss ATR Multiplier 1 → 3, 0.5 | 30 |
| 4 | Lengths | RSI Length 7 → 21, 7 · Volume MA Length 5 → 20, 5 | 12 |
| 5 | Re-check the trigger | pass 1 again, with everything else set | 49 |
| 6 | Unseen months | Standard backtest on the 3 held-back months | 1 |
| 7 | Walk Forward | 60 days optimize / 20 days test. Only the 4 that mattered most, coarse steps, e.g. RSI Lower 15 → 35, 10 · RSI Upper 65 → 85, 10 · TP $ 1000 → 2000, 500 · SL ATR 1.5 → 2.5, 0.5 | 81 per window |
| 8 | Size | choose Stop Loss $ from your account; scale Take Profit $ by the same factor | – |

After each pass, carry the chosen values into the next pass. Rules for choosing:
* Ignore rows with fewer than **100 trades**. Pick from an area where the **neighboring values are also good**, never a lone best row.
* **Only change a setting if it clearly helps**: profit factor up by about 0.1 or more, with a similar trade count. Otherwise keep the original value. Every change you keep is another way to fit the past.
* Before pass 2, read the setup report on your chart. Filters whose "Blocked ONLY by" row shows positive avg R are the ones worth switching off.
* Pass 5: if the best RSI levels jump somewhere else entirely, the result is unstable. Prefer the original levels.
* Pass 6: profit factor should stay above ~1.2 at a similar trade frequency. If it collapses, go back and change fewer settings.
* Pass 7 is the most honest estimate. If its combined test windows lose money, the strategy has no stable edge on this instrument.

**Don't optimize:** Stop Loss $ (a size decision), Max Contracts (a safety cap), ATR Length / Trend SMA Length (hard-coded in Pine; only try them last, if at all), Round Heikin Ashi to tick, or the chart options.

**Tip:** Genetic optimizer (**Optimizer = Genetic**) only if a pass goes above ~2,000 runs. The staged plan above is easier to understand and to trust.

Important:
* The TradingView values (71 / 20 / −39) look like they were optimized on NAS100 CFD tick volume ("Optimized" is in the script's name). There is no reason they are best for NQ volume, so re-optimizing on your own instrument is reasonable. **After you change them, the strategy no longer mirrors TradingView.** Save the defaults first: in the strategy's properties, *Template → Save*, e.g. "TV mirror".
* If "good trades" means trades **TradingView took and NinjaTrader didn't**, optimizing won't fix it. That is the data difference in section 3. Compare the `rsi_source` column (section 6) around those bars.

### PDH / PDL filter

**Use PDH/PDL filter** (group *8. Filter switches*, off by default) adds one more condition to the entry, based on where the signal bar's close sits in the **previous session's range**. **PDH/PDL Range Level (%)** sets the line:

| Level | Longs need the close above | Shorts need the close below |
|---|---|---|
| 100 | PDH (a breakout, as in V3) | PDL |
| **50** (default) | the middle of the range | the middle of the range |
| 25 | a quarter of the way up from PDL | a quarter of the way down from PDH |
| 0 | PDL | PDH |

In general: long line = PDL + level × (PDH − PDL), short line = PDH − level × (PDH − PDL). Everything else is unchanged; with the switch off, every trade is identical. The level can be optimized in the Strategy Analyzer (e.g. 25 → 100, step 25).

* Sessions come from the chart's **Trading Hours** template. With *CME US Index Futures ETH*, one session runs 6 pm to 5 pm New York time, so the levels include the overnight. Sunday evening's previous session is Friday's.
* The first, partial session on the chart is never used. Until one full session has been seen, the filter allows no trades.
* While it is on, the chart shows PDH / PDL as gold lines and the long / short lines as dashed green / red (at 50 they sit on top of each other). The stats box shows the current values. Skipped setups name the blocker (`PDH` / `PDL` at level 100, otherwise e.g. `PD50%`), and the setup report has a *Blocked ONLY by PD range level* row.

### Daily rules & trading hours (group 9, V5)

All off by default. A "day" is one session of the chart's Trading Hours (CME ETH: 6 pm to 5 pm New York), the same day prop firms use.

| Setting | What it does |
|---|---|
| **Daily Profit Goal ($)** | Once today's closed trades reach it, no new trades until the next session. 0 = off. |
| **Daily Goal Tolerance ($)** | The goal also counts this much below it: goal 1500 with tolerance 100 means done from +$1,400. Covers ticks, slippage and fees. |
| **Daily Max Loss ($)** | Once today's closed trades lose this much, no new trades until the next session. 0 = off. |
| **Block trades that could break max loss** | Don't open a trade if hitting its full stop would push today past the Daily Max Loss. Example: max loss $2,000, already −$1,500 today, so a new $1,500-risk trade is skipped. |
| **Use Trading Hours**, **Start Time**, **End Time** | Only open trades between Start and End, by bar close time in the chart's time zone. The window may cross midnight (18:00 → 16:45). |
| **Flatten at End Time** | Close any open trade on the first bar at or after the End Time. |

Details:
* A reversal counts the closed trade first. If that close reaches the goal or the max loss, the strategy just exits instead of reversing. Any other time an opposite signal is blocked, it still closes the open trade ("Signal exit").
* Today's result uses the strategy's own trade prices before commissions (the same numbers as the stats box); the tolerance absorbs the difference. Open trades are limited by their stop orders, not by the daily numbers.
* On the chart: "DAILY GOAL / DAILY MAX LOSS - done for today" at the bar where it happened. Blocked signals show `skip buy: daily goal`, `loss room` or `hours`, and the stats box shows today's result and status.

### Prop accounts dashboard (group 10, V6, simulated)

**Use Account Rotation** turns the strategy's trades into a set of prop evaluations. Each new trade goes to **one** account, and every account follows its firm's rules. A table at the top right of the chart shows every account. This is a **simulation**: no orders go to other accounts yet. The strategy's own orders still trade every trade on the account it runs on.

**Setup**
1. Group 10: **Use Account Rotation** = on.
2. **Accounts**: `name=plan`, separated by commas, e.g. `TS-1=Topstep50K, TS-2=Topstep50K, LU-1=LucidFlex50K`. A wrong plan name is reported at the top of the dashboard.
3. **Rotation Mode**:
   * `EveryTrade`: each new trade goes to the next account in the list.
   * `UntilDayDone`: stay on one account until its day is done (daily goal, a loss limit, or no room for the next trade), then move to the next.
4. Group 9 (daily goal / tolerance / max loss / block) now applies **per account**. Trading hours stay global.

**Plans** (from the firms' published 50K/100K/150K rules as of Oct 2026, so verify before relying on them; all amounts are relative to the starting balance):

| Plan | Profit target | Max loss (trailing end of day, stops at start) | Daily loss limit | Consistency |
|---|---|---|---|---|
| `Topstep50K` | $3,000 | $2,000 | none | best day ≤ 50% of profit |
| `Topstep50K-DLL` | $3,000 | $2,000 | $1,000 | 50% |
| `Topstep100K` | $6,000 | $3,000 | none | 50% |
| `Topstep150K` | $9,000 | $4,500 | none | 50% |
| `LucidFlex50K` | $3,000 | $2,000 | none | 50% |
| `LucidPro50K` | $3,000 | $2,000 | none (the daily limit is optional at purchase) | none |
| `LucidPro50K-DLL` | $3,000 | $2,000 | $1,200 | none |
| `Custom` | the *Custom:* settings in group 10 (any target, max loss, drawdown type, daily limit, consistency) | | | |

**How the rules are simulated**
* **Max loss line:** end-of-day trailing (or intraday trailing / static for Custom). It trails the highest balance and stops at the starting balance. The firm watches open trades in real time, so the line is checked at the **worst price of every bar**. A trade that dips through it **fails the account** even if it recovers later. The trade is closed at the line ("Account max loss").
* **Firm daily loss limit:** checked the same way. Hitting it closes the trade ("Firm daily loss") and ends that account's day, not the evaluation.
* **Passed:** profit ≥ target and, with a consistency rule, best day ≤ X% of total profit (otherwise the required profit rises). **Failed:** max loss line reached.
* **Restart Finished Accounts:** a passed or failed slot starts a new evaluation at the next session, so the totals show **passes per month**, the **pass rate** and the **fees** (*Evaluation Fee*).
* **Block trades that could break max loss** (group 9) also checks each account's firm daily limit. **Block trades that could fail the account** (group 10) skips an account whose remaining room is smaller than the trade's risk.

**Reading the dashboard:** Status is `ACTIVE`, `IN TRADE`, `DONE TODAY (reason)`, `PASSED`, `FAILED`, or `CAN'T FIT $X RISK` (the next trade's full stop would break this account's limit). **Room** is the distance to the max loss line, and **Pass/Fail** counts finished evaluations in that slot. The bottom line shows evaluations, passed, failed, pass rate, average trading days to pass, passes per month and fees. PASSED / FAILED are also marked on the chart. The Output window gets the table when loading finishes. With *Export Trades CSV*, the trades file gets an **Account** column and `<instrument>_accounts.csv` lists every event.

**Things the simulation shows about the $1,500 / $750 plan**
* **A Lucid Pro account *with* the $1,200 daily limit (`LucidPro50K-DLL`) can't take it.** The limit is smaller than the $1,500 risk, so with "Block trades that could break max loss" on it shows `CAN'T FIT` and never trades. Lucid Pro without the daily limit (`LucidPro50K`) and Lucid Flex can. Lucid 50K allows up to 4 minis / 40 micros, which matches Max Contracts = 40.
* **"Block trades that could fail the account" can leave accounts stuck.** After one $1,500 loss only $500 of room is left, so the account never trades again and never passes or fails. Leave it off for this plan, which is what the pass-rate simulation assumed.

**Not modeled:** commissions and slippage, minimum trading days, payout rules, and rule changes by the firms. The end of the day is the end of the chart's session. Best day uses closed trades.

## 8. Differences that cannot be removed

* **Fill price.** No real broker can fill at the close of a bar that has already closed. NinjaTrader's real fills come one tick later; TradingView's simulated fill is the close. The drawn markers and the stats box use TradingView's price; NinjaTrader's Strategy Performance uses the real fills.
* **Start of history.** A position TradingView opened before NinjaTrader's first loaded bar does not exist in NinjaTrader. Both line up from the next signal.
* **Heikin Ashi ties.** If a trade differs only on a bar where HA open and close are within half a tick, toggle **Round Heikin Ashi to tick**. I could not confirm whether TradingView rounds HA values.

## 9. Position size (replaces the martingale)

* `FixedContracts` (default): always *Fixed Contracts*.
* `RiskPerTrade`: `floor(Risk Per Trade $ / (SL ATR multiplier × ATR × point value))`, capped at *Max Contracts*. This is the Pine script's non-martingale "USD" sizing: its `qty = initial_size / (sl_multiplier × atr)` loses `initial_size` at the stop.

With ATR exits, size never changes *when* or *at what price* it trades.

### Dollar stops and targets

**Stop/Target Units = Dollars** replaces the two ATR multipliers with **Stop Loss ($)** and **Take Profit ($)** for the whole position. It always uses *Fixed Contracts*. Dollars become a distance in points:

| | $500 | $1000 | $1500 | $2000 |
|---|---|---|---|---|
| 1 NQ ($20/pt) | 25 pts | 50 pts | 75 pts | 100 pts |
| 1 MNQ ($2/pt) | 250 pts | 500 pts | 750 pts | 1000 pts |
| 2 NQ | 12.5 pts | 25 pts | 37.5 pts | 50 pts |

So with a fixed number of contracts (*Dollars* mode), optimizing the dollars *is* optimizing the stop/target distance. (In *DollarsSizedByAtr* it isn't: there the dollars only set the size. See the plan in section 7.) The difference from ATR: a fixed distance ignores volatility. 25 points is wide in the overnight session and narrow at the 9:30 open. The optimizer can tell you which works better on your data.

### Fixed $ stop and target, contracts sized by ATR (recommended for fixed $)

**Stop/Target Units = DollarsSizedByAtr** keeps the loss at *Stop Loss ($)* and the win at *Take Profit ($)* on every trade, and lets ATR choose the size:

1. contracts = floor(Stop Loss $ / (Stop Loss ATR Multiplier × ATR × point value)), between 1 and *Max Contracts*;
2. stop and target are placed so that this many contracts lose exactly Stop Loss $ / win exactly Take Profit $ (rounded to the tick).

Quiet market: tight stop, more contracts. Fast market: wide stop, fewer contracts. Same dollars either way. *Stop Loss ATR Multiplier* now only sets roughly how wide the stop should be.

**This needs MNQ, not NQ.** Size can only change in whole contracts. With a $1000 stop and a 2 ATR stop:

| 5-min ATR | Ideal stop | NQ ($20/pt) | MNQ ($2/pt) |
|---|---|---|---|
| 10 pts | 20 pts | 2 contracts → stop forced to 25 pts | 25 contracts → 20 pts |
| 20 pts | 40 pts | 1 contract → stop forced to 50 pts | 12 contracts → 41.75 pts |
| 40 pts | 80 pts | can't size below 1 → stop forced to 50 pts | 6 contracts → 83.25 pts |

On NQ the size barely changes, so the stop ends up wherever the dollars put it. On MNQ it follows the ATR closely. Raise **Max Contracts** for MNQ: quiet overnight bars can need 25–50 MNQ for $1000 of risk. When the cap (or the 1-contract minimum) is hit, the dollars stay fixed and the stop moves off the ATR distance. The stats box and the setup report show *Contracts per entry: min / max / avg* and how often each limit was hit.

**Exit Execution**:
* `OnBarClose` (TradingView): exits at the close once price is past the level. A "$500" stop can lose more on a fast bar.
* `StopTargetOrders`: real stop and target orders (OCO) at the exact levels. A stop fills at the level, or worse only on a gap. If one bar touches both levels, the chart markers and stats box assume the stop filled first; NinjaTrader's backtest decides on its own.

**Choosing contracts:** pick the *distance* first (that's what the optimizer tests). Then set contracts from your account: risking about 0.5–1% per trade is common. Don't optimize contracts: more contracts only scale profit and loss up and down.

**Break-even win rate** = loss / (loss + win):

| Loss \ Win | $500 | $1000 | $1500 |
|---|---|---|---|
| **$500** | 50% | 33% | 25% |
| **$1000** | 67% | 50% | 40% |
| **$1500** | 75% | 60% | 50% |
| **$2000** | 80% | 67% | 57% |

## 10. How the port was verified

`tools/verify/run.sh` (needs the .NET 8 SDK and Python 3):

* compiles the shipped `RutaCryptoMirror.cs` as **C# 5**, NinjaTrader 8's language level, against stand-ins for the NinjaTrader API;
* runs it on 6 synthetic 5-minute datasets and checks that every TradingView fill produces exactly one NinjaTrader order on the same bar, and that `Calculate = OnEachTick` gives the same trades as `OnBarClose`;
* recomputes everything with an independent Python version of the Pine script. Result: **0 mismatches over 36,000 bars and 295 trades**, on every indicator value, both conditions, the position, and every entry and exit time, price and reason.
* recomputes the dollar-exit modes ($500 stop / $1000 target, both on-close and with stop/target orders) and the ATR-sized mode (MNQ, $1000 / $1500, capped at 12): same trades, contract counts, setups and report rows;
* recomputes the PDH/PDL levels and the long / short lines, and runs with the filter on at levels 50 and 100: same values on every bar, same trades, setups and report rows. With the filter off, 84 output files (trades in every exit mode, per-bar values, setups, report rows) are identical to the version before the filter was added. At level 100, the trades, setups and report rows are identical to V3's filter;
* recomputes the daily rules and trading hours: the planned prop setup (MNQ, $1,500 / $750, PD 50%, goal $1,500 − $100, max loss $2,000), on-close exits with daily rules, an 18:00 → 16:45 window with flatten and a 09:30 → 16:00 window without. Every trade, setup and report row matches. With all of them off, 126 output files are identical to V4;
* recomputes the prop account rotation: 4 configurations (the planned setup on Topstep + Lucid accounts rotating every trade; until the day is done with the account-loss block; Topstep with its daily limit, a custom intraday-trailing account and Lucid Pro; on-close exits with a custom static account and Topstep 100K without restarts). On all 6 datasets every trade, the account that took it, and every account event (start, day done, passed, failed with balances) match. With rotation off, 198 output files are identical to V5;
* recomputes the setup report the same way: all 848 RSI-trigger setups (filters, blocker, outcome, R) and every report row match. A run with the SMA filter switched off matches too. Every real TP/SL trade has the same hindsight outcome as its setup.

This proves the C# follows the Pine script exactly. It does not replace the real side-by-side check in section 3: neither TradingView nor NinjaTrader can run in this environment, so the compile was against stand-ins, not NinjaTrader itself.
