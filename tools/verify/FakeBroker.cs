// Simulated broker for the Prop Account Manager router: accounts, positions, cash, market / stop / limit orders,
// OCO, and switches to reject, ignore cancels, disconnect, or leave a position after Flatten.
using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns;

public sealed class FakeOrder
{
	public int Id;
	public string Account, Name, Oco, Kind;		// Kind: MKT, STP, LMT
	public bool Buy, Closing;
	public int Quantity, Filled;
	public double StopPrice, LimitPrice, AvgPrice = double.NaN;
	public RutaPropOrderState State = RutaPropOrderState.Working;

	public override string ToString()
	{
		return Account + " " + Kind + " " + (Buy ? "BUY " : "SELL ") + Quantity + (Kind == "STP" ? " stop " + StopPrice : Kind == "LMT" ? " limit " + LimitPrice : "") + " " + State;
	}
}

public sealed class FakeBroker : IRutaPropBroker
{
	public double Price = 20000, TickSize = 0.25, PointValue = 2, StartCash = 50000;
	public readonly HashSet<string> Connected = new HashSet<string>();
	public readonly Dictionary<string, int> Pos = new Dictionary<string, int>();
	public readonly Dictionary<string, double> Avg = new Dictionary<string, double>();
	public readonly Dictionary<string, double> Realized = new Dictionary<string, double>();
	public readonly Dictionary<string, int> Foreign = new Dictionary<string, int>();	// positions in other instruments
	public readonly List<FakeOrder> Orders = new List<FakeOrder>();
	public bool FillMarketOnSubmit, HoldMarket, RejectNextMarket, RejectNextOco, IgnoreCancels, NoOcoCancel, FlattenDoesNothing, ReportCash = true;
	public int FlattenCalls;
	private int nextId = 1;

	public FakeBroker(params string[] accounts)
	{
		foreach (string a in accounts)
			Connected.Add(a);
	}

	private int P(string a) { int v; return Pos.TryGetValue(a, out v) ? v : 0; }
	private double R(string a) { double v; return Realized.TryGetValue(a, out v) ? v : 0; }

	public List<string> AccountNames() { return Connected.OrderBy(x => x).ToList(); }
	public bool IsConnected(string account) { return Connected.Contains(account); }
	public double CashValue(string account) { return ReportCash && Connected.Contains(account) ? StartCash + R(account) : double.NaN; }
	public int Position(string account, Instrument instrument) { return Connected.Contains(account) ? P(account) : int.MaxValue; }
	public int OpenPositions(string account)
	{
		int f;
		return (P(account) != 0 ? 1 : 0) + (Foreign.TryGetValue(account, out f) && f != 0 ? 1 : 0);
	}

	public object SubmitMarket(string account, Instrument instrument, bool buy, bool closing, int quantity, string name)
	{
		if (!Connected.Contains(account))
			return null;
		FakeOrder o = NewOrder(account, "MKT", buy, quantity, name, null);
		o.Closing = closing;
		if (RejectNextMarket)
		{
			RejectNextMarket = false;
			o.State = RutaPropOrderState.Rejected;
		}
		else if (FillMarketOnSubmit)
			Process();
		return o;
	}

	public object[] SubmitOco(string account, Instrument instrument, bool closeLong, int quantity, double stopPrice, double targetPrice, string oco)
	{
		if (!Connected.Contains(account))
			return null;
		FakeOrder stop = NewOrder(account, "STP", !closeLong, quantity, "RP Stop", oco);
		stop.StopPrice = stopPrice;
		FakeOrder target = NewOrder(account, "LMT", !closeLong, quantity, "RP Target", oco);
		target.LimitPrice = targetPrice;
		if (RejectNextOco)
		{
			RejectNextOco = false;
			stop.State = RutaPropOrderState.Rejected;		// e.g. stop price already on the wrong side of the market
			target.State = RutaPropOrderState.Cancelled;	// OCO: the broker cancels the other one
		}
		else
			Process();
		return new object[] { stop, target };
	}

	public RutaPropOrderState GetState(object order, out int filled, out double averagePrice)
	{
		FakeOrder o = (FakeOrder)order;
		filled = o.Filled;
		averagePrice = o.AvgPrice;
		return o.State;
	}

	public void Cancel(string account, object order)
	{
		FakeOrder o = (FakeOrder)order;
		if (!IgnoreCancels && o.State == RutaPropOrderState.Working)
			o.State = RutaPropOrderState.Cancelled;
	}

	public void Flatten(string account, Instrument instrument)
	{
		FlattenCalls++;
		if (FlattenDoesNothing)
			return;
		foreach (FakeOrder o in Orders)
			if (o.Account == account && o.State == RutaPropOrderState.Working)
				o.State = RutaPropOrderState.Cancelled;
		int p = P(account);
		if (p != 0)
			Apply(account, p < 0, Math.Abs(p), Price);
	}

	public double RoundToTick(Instrument instrument, double price) { return Math.Round(price / TickSize) * TickSize; }

	public void SetPrice(double price)
	{
		Price = price;
		Process();
	}

	public void Process()
	{
		foreach (FakeOrder o in Orders.ToArray())
		{
			if (o.State != RutaPropOrderState.Working)
				continue;
			if (o.Kind == "MKT")
			{
				if (!HoldMarket)
					Fill(o, Price);
			}
			else if (o.Kind == "STP")
			{
				if (o.Buy ? Price >= o.StopPrice : Price <= o.StopPrice)
					Fill(o, Price);
			}
			else if (o.Buy ? Price <= o.LimitPrice : Price >= o.LimitPrice)
				Fill(o, o.LimitPrice);
		}
	}

	public void FillPartially(FakeOrder o, int quantity, double price)
	{
		o.AvgPrice = price;
		o.Filled = quantity;
		Apply(o.Account, o.Buy, quantity, price);
	}

	private void Fill(FakeOrder o, double price)
	{
		int add = o.Quantity - o.Filled;
		o.AvgPrice = o.Filled > 0 ? (o.AvgPrice * o.Filled + price * add) / o.Quantity : price;
		o.Filled = o.Quantity;
		o.State = RutaPropOrderState.Filled;
		Apply(o.Account, o.Buy, add, price);
		if (!string.IsNullOrEmpty(o.Oco) && !NoOcoCancel)
			foreach (FakeOrder x in Orders)
				if (x != o && x.Oco == o.Oco && x.State == RutaPropOrderState.Working)
					x.State = RutaPropOrderState.Cancelled;
	}

	private void Apply(string account, bool buy, int quantity, double price)
	{
		int p = P(account);
		double avg;
		Avg.TryGetValue(account, out avg);
		int q = buy ? quantity : -quantity;
		if (p == 0 || Math.Sign(p) == Math.Sign(q))
		{
			avg = (avg * Math.Abs(p) + price * Math.Abs(q)) / (Math.Abs(p) + Math.Abs(q));
			p += q;
		}
		else
		{
			int closed = Math.Min(Math.Abs(p), Math.Abs(q));
			Realized[account] = R(account) + closed * (price - avg) * Math.Sign(p) * PointValue;
			int before = p;
			p += q;
			if (p == 0)
				avg = 0;
			else if (Math.Sign(p) != Math.Sign(before))
				avg = price;
		}
		Pos[account] = p;
		Avg[account] = avg;
	}

	private FakeOrder NewOrder(string account, string kind, bool buy, int quantity, string name, string oco)
	{
		FakeOrder o = new FakeOrder();
		o.Id = nextId++;
		o.Account = account;
		o.Kind = kind;
		o.Buy = buy;
		o.Quantity = quantity;
		o.Name = name;
		o.Oco = oco;
		Orders.Add(o);
		return o;
	}

	public List<FakeOrder> Working(string account)
	{
		return Orders.Where(o => o.Account == account && o.State == RutaPropOrderState.Working).ToList();
	}
}
