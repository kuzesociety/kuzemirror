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

### Optimizing in NinjaTrader

1. Control Center → **New → Strategy Analyzer**. Choose RutaCryptoMirror, your instrument, 5 minutes, and the same trading hours as the chart.
2. **Backtest type = Optimization.** Every parameter then gets Min / Max / Increment. Start with 2–3 parameters at a time, for example:
   * RSI Lower Level 10 → 35 step 5, RSI Upper Level 60 → 85 step 5
   * Volume Increase Threshold −60 → 20 step 10
   * Stop Loss ATR 1 → 3 step 0.5, Take Profit ATR 2 → 6 step 1
   * RSI Length 7 → 21 step 7, Volume MA Length 5 → 20 step 5
   * the three filter switches (true / false)
3. **Optimize on** *Max profit factor* or *Max Sharpe ratio*, not *Max net profit* (which favors a few lucky trades). In the results, ignore any combination with fewer than ~50 trades.
4. For many parameters at once, set **Optimizer = Genetic**.
5. Guard against curve fitting: optimize on older data (e.g. 3 months), then run the winner **once** on the next month it has never seen. Better still, use **Backtest type = Walk Forward** (e.g. 20 days optimize / 5 days test). A setting that only works in-sample is noise.
6. Turn off the *Show …* chart options in the analyzer; it runs faster.

Important:
* The TradingView values (71 / 20 / −39) look like they were optimized on NAS100 CFD tick volume ("Optimized" is in the script's name). There is no reason they are best for NQ volume, so re-optimizing on your own instrument is reasonable. **After you change them, the strategy no longer mirrors TradingView.** Save the defaults first: in the strategy's properties, *Template → Save*, e.g. "TV mirror".
* If "good trades" means trades **TradingView took and NinjaTrader didn't**, optimizing won't fix it. That is the data difference in section 3. Compare the `rsi_source` column (section 6) around those bars.

## 8. Differences that cannot be removed

* **Fill price.** No real broker can fill at the close of a bar that has already closed. NinjaTrader's real fills come one tick later; TradingView's simulated fill is the close. The drawn markers and the stats box use TradingView's price; NinjaTrader's Strategy Performance uses the real fills.
* **Start of history.** A position TradingView opened before NinjaTrader's first loaded bar does not exist in NinjaTrader. Both line up from the next signal.
* **Heikin Ashi ties.** If a trade differs only on a bar where HA open and close are within half a tick, toggle **Round Heikin Ashi to tick**. I could not confirm whether TradingView rounds HA values.

## 9. Position size (replaces the martingale)

* `FixedContracts` (default): always *Fixed Contracts*.
* `RiskPerTrade`: `floor(Risk Per Trade $ / (SL ATR multiplier × ATR × point value))`, capped at *Max Contracts*. This is the Pine script's non-martingale "USD" sizing: its `qty = initial_size / (sl_multiplier × atr)` loses `initial_size` at the stop.

Size never changes *when* or *at what price* it trades.

## 10. How the port was verified

`tools/verify/run.sh` (needs the .NET 8 SDK and Python 3):

* compiles the shipped `RutaCryptoMirror.cs` as **C# 5**, NinjaTrader 8's language level, against stand-ins for the NinjaTrader API;
* runs it on 6 synthetic 5-minute datasets and checks that every TradingView fill produces exactly one NinjaTrader order on the same bar, and that `Calculate = OnEachTick` gives the same trades as `OnBarClose`;
* recomputes everything with an independent Python version of the Pine script. Result: **0 mismatches over 36,000 bars and 295 trades**, on every indicator value, both conditions, the position, and every entry and exit time, price and reason.
* recomputes the setup report the same way: all 848 RSI-trigger setups (filters, blocker, outcome, R) and every report row match. A run with the SMA filter switched off matches too. Every real TP/SL trade has the same hindsight outcome as its setup.

This proves the C# follows the Pine script exactly. It does not replace the real side-by-side check in section 3: neither TradingView nor NinjaTrader can run in this environment, so the compile was against stand-ins, not NinjaTrader itself.
