using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Reflection;
using System.Xml;
using System.Security.Cryptography;

namespace Detail
{
    public enum Direction
    {
        NEUTRAL = 0,
        BUY = 1,
        SELL = 2
    }
}

namespace StrategyRunner
{
    public class BVConfig
    {
        public string nearInstrument;
        public string farInstrument;
        public string leanInstrument;
        public int? limitPlusSize;
        public int? nonLeanLimitPlusSize;
        public double? defaultBaseSpread;
        public List<string> crossVenueInstruments { get; set; }
        public List<string> correlatedInstruments { get; set; }

        public BVConfig(string nearInstrument, string farInstrument, string leanInstrument, int? limitPlusSize, int? nonLeanLimitPlusSize, double? defaultBaseSpread)
        {
            this.nearInstrument = nearInstrument;
            this.farInstrument = farInstrument;
            this.leanInstrument = leanInstrument;
            this.limitPlusSize = limitPlusSize;
            this.nonLeanLimitPlusSize = nonLeanLimitPlusSize;
            this.defaultBaseSpread = defaultBaseSpread;
            crossVenueInstruments = new List<string>();
            correlatedInstruments = new List<string>();
        }
    }

    public class BV : Strategy
    {
        private BVConfig config;

        public double BVBuyPrice;
        public double BVSellPrice; //POTENTIALLY BVSellPrice < BVBuyPrice IF THERE'S ARBITRAGE
        public int BVBuyIndex;
        public int BVSellIndex;

        public double limitBVSellPrice;
        public double limitBVBuyPrice;
        public int limitBVBuyIndex;
        public int limitBVSellIndex;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        VI nearInstrument;
        VI farInstrument;

        KGOrder buy;
        KGOrder sell;

        KGOrder buyFar;
        KGOrder sellFar;

        List<VI> instruments;
        Dictionary<int /*orderId*/, int /*amount*/> pendingOrders;
        Dictionary<int /*orderId*/, int /*volume*/> pendingResubmissions;

        Throttler.Throttler bvThrottler;

        Timer timeout;
        bool shouldHedge = false;

        Hedging hedging;

        Dictionary<int, int> pendingTrades;

        public static int bvThrottleSeconds = -1;
        public static int bvThrottleVolume = -1;
        public static double creditOffset = -1;
        public static int maxCrossVolume = -1;
        public static int maxOutrights = -1;
        public static int bvMaxOutstandingOutrights = -1;
        public static double bvTimeoutSeconds = -1;
        public static double bvMaxLoss = -1;

        public BV(API api, BVConfig config)
        {
            try
            {
                this.config = config;

                API = api;
                API.Log("-->strategy:" + config.nearInstrument);

                systemTradingMode = 'C';

                numAllInstruments = API.N;
                numInstrumentsInVenue = API.n;

                holding = new int[numAllInstruments];
                bids = new DepthElement[numAllInstruments];
                asks = new DepthElement[numAllInstruments];

                for (int i = 0; i < numAllInstruments; i++)
                {
                    holding[i] = 0;
                    bids[i] = new DepthElement(-11, 0);
                    asks[i] = new DepthElement(11111, 0);
                }

                if (config.limitPlusSize.HasValue)
                    limitPlusSize = config.limitPlusSize.Value;
                else
                    limitPlusSize = 300;

                if (config.nonLeanLimitPlusSize.HasValue)
                    nonLeanLimitPlusSize = config.nonLeanLimitPlusSize.Value;
                else
                    nonLeanLimitPlusSize = 50;

                if (config.defaultBaseSpread.HasValue)
                    boxTargetPrice = config.defaultBaseSpread.Value;
                else
                    boxTargetPrice = 0;

                instruments = new List<VI>();
                pendingOrders = new Dictionary<int, int>();

                quoteIndex = API.GetSecurityIndex(config.nearInstrument);
                farIndex = API.GetSecurityIndex(config.farInstrument);
                leanIndex = API.GetSecurityIndex(config.leanInstrument);

                tickSize = API.GetTickSize(quoteIndex);

                int nearVenue = quoteIndex / numInstrumentsInVenue;
                int nearIndexGlobal = quoteIndex % numInstrumentsInVenue;

                int farVenue = farIndex / numInstrumentsInVenue;
                int farIndexGlobal = farIndex % numInstrumentsInVenue;

                nearInstrument = new VI(nearVenue, nearIndexGlobal);
                farInstrument = new VI(farVenue, farIndexGlobal);

                crossVenueIndices = new List<int>();
                correlatedIndices = new List<int>();
                foreach (var instrument in config.crossVenueInstruments)
                {
                    int index = API.GetSecurityIndex(instrument);
                    int venue = index / numInstrumentsInVenue;
                    int indexGlobal = index % numInstrumentsInVenue;
                    instruments.Add(new VI(venue, indexGlobal));
                    crossVenueIndices.Add(index);
                }
                foreach (var instrument in config.correlatedInstruments)
                {
                    int index = API.GetSecurityIndex(instrument);
                    int venue = index / numInstrumentsInVenue;
                    int indexGlobal = index % numInstrumentsInVenue;
                    instruments.Add(new VI(venue, indexGlobal));
                    correlatedIndices.Add(index);
                }

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);

                buyFar = new KGOrder();
                strategyOrders.Add(buyFar);
                sellFar = new KGOrder();
                strategyOrders.Add(sellFar);

                double bvThrottleMs = GetBvThrottleSeconds() * 1000;
                TimeSpan tBv = new TimeSpan(0, 0, 0, 0, (int)bvThrottleMs);

                bvThrottler = new Throttler.Throttler(GetBvThrottleVolume(), tBv);

                timeout = new Timer();
                timeout.Elapsed += OnTimeout;
                timeout.AutoReset = false;

                pendingTrades = new Dictionary<int, int>();
                pendingResubmissions = new Dictionary<int, int>();

                double ms = GetEurexThrottleSeconds() * 1000;
                TimeSpan t = new TimeSpan(0, 0, 0, 0, (int)ms);
                eurexThrottler = new Throttler.EurexThrottler(GetEurexThrottleVolume(), t);

                orders = new Orders(this);
                hedging = new Hedging(this);

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 0, 5);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private double GetBvThrottleSeconds()
        {
            if (bvThrottleSeconds == -1)
                return P.bvThrottleSeconds;
            return bvThrottleSeconds;
        }

        private int GetBvThrottleVolume()
        {
            if (bvThrottleVolume == -1)
                return P.bvThrottleVolume;
            return bvThrottleVolume;
        }

        private double GetCreditOffset()
        {
            if (creditOffset == -1)
                return P.creditOffset;
            return creditOffset;
        }

        private int GetMaxCrossVolume()
        {
            if (maxCrossVolume == -1)
                return P.maxCrossVolume;
            return maxCrossVolume;
        }

        private int GetMaxOutrights()
        {
            if (maxOutrights == -1)
                return P.maxOutrights;
            return maxOutrights;
        }

        private int GetBvMaxOutstandingOutrights()
        {
            if (bvMaxOutstandingOutrights == -1)
                return P.bvMaxOutstandingOutrights;
            return bvMaxOutstandingOutrights;
        }

        private double GetBvTimeoutSeconds()
        {
            if (bvTimeoutSeconds == -1)
                return P.bvTimeoutSeconds;
            return bvTimeoutSeconds;
        }

        public override double GetMaxLossMarketHedge()
        {
            if (maxLossMarketHedge == -1)
                return P.maxLossMarketHedge;
            return maxLossMarketHedge;
        }

        public override double GetMaxLossLimitHedge()
        {
            if (maxLossMarketHedge == -1)
                return P.maxLossLimitHedge;
            return maxLossLimitHedge;
        }

        private double GetEurexThrottleSeconds()
        {
            if (eurexThrottleSeconds == -1)
                return P.eurexThrottleSeconds;
            return eurexThrottleSeconds;
        }

        private int GetEurexThrottleVolume()
        {
            if (eurexThrottleVolume == -1)
                return P.eurexThrottleVolume;
            return eurexThrottleVolume;
        }

        private void Log(string message)
        {
            API.Log(String.Format("STG {0}: {1}", stgID, message));
            API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        public override void OnStatusChanged(int status)
        {
            if (status == 0)
            {
                API.CancelAllOrders(stgID);
            }
            if (status == 1)
            {
                hedging.Hedge();
            }
        }

        public override void OnSystemTradingMode(ref char c)
        {
            systemTradingMode = c;

            if (c == 'C')
            {
                if (API.GetStrategyStatus(stgID) != 0)
                {
                    API.SetStrategyStatus(stgID, 0);
                    API.CancelAllOrders(stgID);
                }
            }
        }

        public static void UpdateConfig(double newBaseSpreadValue, string instrument)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load("bv.xml");

            XmlNode bvNode = xmlDoc.SelectSingleNode($"//BV[nearInstrument='{instrument}']");

            if (bvNode != null)
            {
                XmlNode baseSpreadNode = bvNode.SelectSingleNode("defaultBaseSpread");

                if (baseSpreadNode != null)
                {
                    baseSpreadNode.InnerText = newBaseSpreadValue.ToString();
                    xmlDoc.Save("bv.xml");
                }
            }
        }

        public override void OnFlush()
        {
            UpdateConfig(boxTargetPrice, config.nearInstrument);
        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
        }

        public override int GetNetPosition()
        {
            int netHolding = 0;

            foreach (var instrument in correlatedIndices)
            {
                netHolding += holding[instrument];
            }

            foreach (var instrument in crossVenueIndices)
            {
                netHolding += holding[instrument];
            }

            return netHolding;
        }


        private int GetQuotedPosition()
        {
            return holding[quoteIndex] + holding[farIndex];
        }

        private int Buy(int n, string source, int index, double price)
        {
            int orderId = orders.SendOrder(buy, index, Side.BUY, price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }
        private int Sell(int n, string source, int index, double price)
        {
            int orderId = orders.SendOrder(sell, index, Side.SELL, price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }

        private bool isBuyPreferred(double BVPrice)
        {
            if (BVPrice - API.GetImprovedCM(leanIndex) < boxTargetPrice - GetCreditOffset())
                return true;
            else
                return false;
        }

        private bool isSellPreferred(double BVPrice)
        {
            if (BVPrice - API.GetImprovedCM(leanIndex) > boxTargetPrice + GetCreditOffset())
                return true;
            else
                return false;
        }

        private void HedgeLeftovers()
        {
            int position = GetNetPosition();

            API.Log(String.Format("STG {0}: HedgeLeftovers, position={1}", stgID, position));

            if (position == 0)
                return;

            foreach (var deal in pendingOrders)
            {
                orders.CancelOrder(deal.Key);
            }

            hedging.Hedge();
            shouldHedge = true;
        }

        private void OnTimeout(object sender, ElapsedEventArgs e)
        {
            try
            {
                HedgeLeftovers();
                pendingTrades.Clear();
            }
            catch (Exception ex)
            {
                API.Log("ERR: " + ex.ToString() + "," + ex.StackTrace);
            }
        }

        private void TakeCross()
        {
            if (bids[quoteIndex].price >= asks[farIndex].price)
            {
                BVSellPrice = bids[quoteIndex].price;
                BVBuyPrice = asks[farIndex].price;

                if (GetQuotedPosition() < -GetBvMaxOutstandingOutrights())
                    return;

                if (holding[leanIndex] > GetMaxOutrights())
                    return;

                timeout.Stop();
                timeout.Start();
                shouldHedge = false;

                int availableVolume = Math.Min(API.GetBid(nearInstrument).qty, API.GetAsk(farInstrument).qty);
                int volume = Math.Min(availableVolume, P.maxCrossVolume);

                if (volume == 0)
                {
                    return;
                }

                if (Math.Abs(holding[quoteIndex] - volume) > GetMaxOutrights() || Math.Abs(holding[farIndex] + volume) > GetMaxOutrights())
                {
                    return;
                }

                //if (!bvThrottler.addTrade(volume))
                //    return;

                if (isSellPreferred(BVSellPrice))
                {
                    BVSellIndex = quoteIndex;
                    BVBuyIndex = farIndex;
                    Sell(volume, "BV", BVSellIndex, BVSellPrice);
                    Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                }
                else if (isBuyPreferred(BVBuyPrice))
                {
                    BVSellIndex = quoteIndex;
                    BVBuyIndex = farIndex;
                    int orderId = Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                    pendingTrades[BVBuyIndex] = -volume;
                }
            }

            if (asks[quoteIndex].price <= bids[farIndex].price)
            {
                BVSellPrice = bids[farIndex].price;
                BVBuyPrice = asks[quoteIndex].price;

                if (GetQuotedPosition() > GetBvMaxOutstandingOutrights())
                    return;

                if (holding[leanIndex] < -GetMaxOutrights())
                    return;

                timeout.Stop();
                timeout.Start();
                shouldHedge = false;

                int availableVolume = Math.Min(API.GetAsk(nearInstrument).qty, API.GetBid(farInstrument).qty);
                int volume = Math.Min(availableVolume, GetMaxCrossVolume());

                if (volume == 0)
                {
                    return;
                }

                if (Math.Abs(holding[farIndex] + volume) > GetMaxOutrights() || Math.Abs(holding[quoteIndex] - volume) > GetMaxOutrights())
                {
                    return;
                }

                //if (!bvThrottler.addTrade(volume))
                //    return;

                if (isBuyPreferred(BVBuyPrice))
                {
                    BVSellIndex = farIndex;
                    BVBuyIndex = quoteIndex;
                    Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                    Sell(volume, "BV", BVSellIndex, BVSellPrice);
                }
                else if (isSellPreferred(BVSellPrice))
                {
                    BVSellIndex = farIndex;
                    BVBuyIndex = quoteIndex;
                    int orderId = Sell(volume, "BV", BVSellIndex, BVSellPrice);
                    pendingTrades[BVSellIndex] = volume;
                }
            }
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                VI vi = new VI(vit.v, vit.i);
                int instrumentIndex = vi.i + API.n * vi.v;

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);

                if (!API.PassedTradeStart())
                {
                    return;
                }

                if (API.GetStrategyStatus(stgID) == 0 || systemTradingMode == 'C')
                {
                    return;
                }

                orders.OnProcessMD();
                orders.CheckPendingCancels();
                hedging.CheckIOC();

                if (GetNetPosition() != 0)
                {
                    if (shouldHedge)
                        hedging.Hedge();
                    return;
                }

                if (pendingTrades.Count == 0 && !orders.orderInUse(buy) && !orders.orderInUse(sell))
                {
                    TakeCross();
                }
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate(string paramName, string paramValue)
        {
            SetValue(paramName, paramValue);
        }

        public override void OnGlobalParamsUpdate()
        {
            bvThrottler.updateMaxVolume(GetBvThrottleVolume());
            bvThrottler.updateTimespan(GetBvThrottleSeconds());

            eurexThrottler.updateMaxVolume(GetEurexThrottleVolume());
            eurexThrottler.updateTimespan(GetEurexThrottleSeconds());

            double bvTimeoutSeconds = GetBvTimeoutSeconds();
            if (bvTimeoutSeconds > 0)
            {
                timeout.Interval = bvTimeoutSeconds * 1000;
            }
        }

        private void CheckPending(int instrumentIndex, int amount)
        {
            API.Log(String.Format("STG {0}: [CheckPending] instrument={1}", stgID, instrumentIndex));
            if (pendingTrades.ContainsKey(instrumentIndex))
            {
                if (amount > 0)
                {
                    API.Log(String.Format("STG {0}: [CheckPending] instrument={1} found, buy amount={2} instrument={3} price={4}", stgID, instrumentIndex, amount, BVBuyIndex, BVBuyPrice));
                    int orderId = Buy(amount, "BV", BVBuyIndex, BVBuyPrice);
                    pendingTrades[instrumentIndex] -= amount;
                    if (orderId == -1)
                    {
                        API.Log(String.Format("STG {0}: [CheckPending] send order failed, will retry", stgID));
                        pendingResubmissions[buy.internalOrderNumber] = amount;
                    }
                }
                else if (amount < 0)
                {
                    API.Log(String.Format("STG {0}: [CheckPending] instrument={1} found, sell amount={2} instrument={3} price={4}", stgID, instrumentIndex, amount, BVSellIndex, BVSellPrice));
                    int orderId = Sell(-amount, "BV", BVSellIndex, BVSellPrice);
                    pendingTrades[instrumentIndex] += amount;
                    if (orderId == -1)
                    {
                        API.Log(String.Format("STG {0}: [CheckPending] send order failed, will retry", stgID));
                        pendingResubmissions[sell.internalOrderNumber] = amount;
                    }
                }
                pendingTrades.Remove(instrumentIndex);
            }
        }
        public override void OnDeal(KGDeal deal)
        {
            try
            {
                int instrumentIndex = deal.index + API.n * deal.VenueID;

                API.Log(String.Format("STG {0}: OnDeal instrument={1} order_id={2} source={3}", stgID, instrumentIndex, deal.internalOrderNumber, deal.source));

                if (deal.source == "FW")
                    return;

                if (pendingOrders.ContainsKey(deal.internalOrderNumber))
                {
                    pendingOrders[deal.internalOrderNumber] -= deal.amount;
                    if (pendingOrders[deal.internalOrderNumber] == 0)
                    {
                        pendingOrders.Remove(deal.internalOrderNumber);
                    }
                }

                int amount = deal.isBuy ? deal.amount : -deal.amount;

                holding[instrumentIndex] += amount;

                if (deal.source == "HEDGE" || deal.source == "INTERNAL")
                {
                    hedging.Hedge();
                }

                CheckPending(instrumentIndex, -amount);
            }
            catch (Exception e)
            {
                CancelStrategy(String.Format("OnDeal exception: {1}", stgID, e.ToString()));
            }
        }

        public override void OnPurgedOrder(KGOrder ord)
        {
            CancelStrategy(String.Format("order {0} purged by exchange", ord.internalOrderNumber));
        }

        public override void OnOrder(KGOrder ord)
        {
            API.Log(String.Format("STG {0}: OnOrder int={1} status={2} sec={3} stg={4} ask_size={5} bid_size={6}", stgID, ord.internalOrderNumber, ord.orderStatus, ord.securityNumber, ord.stgID, ord.askSize, ord.bidSize));

            if (pendingResubmissions.ContainsKey(ord.internalOrderNumber))
            {
                int size = pendingResubmissions[ord.internalOrderNumber];
                pendingResubmissions.Remove(ord.internalOrderNumber);

                API.Log(String.Format("STG {0}: retry for int={1}", stgID, ord.internalOrderNumber));

                if (size < 0)
                {
                    Buy(-size, "RETRY", ord.index, ord.ask);
                }
                else if (size > 0)
                {
                    Sell(size, "RETRY", ord.index, ord.bid);
                }
            }
        }

        public static (bool, string) SetValue(string paramName, string paramValue)
        {
            string ret = "";
            bool found = false;
            bool valueChanged = false;
            foreach (FieldInfo field in typeof(BV).GetFields())
            {
                if (field.Name != paramName)
                    continue;
                else
                {
                    found = true;
                    if (field.FieldType == typeof(int))
                    {
                        int val = Int32.Parse(paramValue);
                        valueChanged = val != (int)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(string))
                    {
                        valueChanged = paramValue != (string)field.GetValue(null);
                        field.SetValue(null, paramValue);
                    }
                    else if (field.FieldType == typeof(double))
                    {
                        double val = Double.Parse(paramValue);
                        valueChanged = val != (double)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        if (paramValue == "true")
                        {
                            valueChanged = !(bool)field.GetValue(null);
                            field.SetValue(null, true);
                        }
                        else
                        {
                            valueChanged = (bool)field.GetValue(null);
                            field.SetValue(null, false);
                        }
                    }
                    else if (field.FieldType == typeof(long))
                    {
                        long val = long.Parse(paramValue);
                        valueChanged = val != (long)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    break;
                } //end else
            } // end foreach

            return (valueChanged, ret);
        }
    }
}