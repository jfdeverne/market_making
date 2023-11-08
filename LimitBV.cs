using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Xml;
using System.Reflection;
using System.Reflection.Emit;

namespace StrategyRunner
{
    public class LimitBV : Strategy
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
        VI leanInstrument;

        KGOrder buy;
        KGOrder sell;

        KGOrder limitBuy;
        KGOrder limitSell;

        KGOrder limitBuyFar;
        KGOrder limitSellFar;

        List<VI> instruments;
        Dictionary<int /*orderId*/, int /*amount*/> pendingOrders;
        Dictionary<int /*orderId*/, int /*volume*/> pendingResubmissions;

        Throttler.Throttler bvThrottler;

        Timer timeout;

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

        public LimitBV(API api, BVConfig config)
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

                limitPlusSize = config.limitPlusSize;
                boxTargetPrice = config.defaultBaseSpread;

                instruments = new List<VI>();
                pendingOrders = new Dictionary<int, int>();

                quoteIndex = API.GetSecurityIndex(config.nearInstrument);
                farIndex = API.GetSecurityIndex(config.farInstrument);
                leanIndex = API.GetSecurityIndex(config.leanInstrument);

                int nearVenue = quoteIndex / numInstrumentsInVenue;
                int nearIndexGlobal = quoteIndex % numInstrumentsInVenue;

                int farVenue = farIndex / numInstrumentsInVenue;
                int farIndexGlobal = farIndex % numInstrumentsInVenue;

                int leanVenue = leanIndex / numInstrumentsInVenue;
                int leanIndexGlobal = leanIndex % numInstrumentsInVenue;

                nearInstrument = new VI(nearVenue, nearIndexGlobal);
                farInstrument = new VI(farVenue, farIndexGlobal);
                leanInstrument = new VI(leanVenue, leanIndexGlobal);

                instruments.Add(nearInstrument);
                instruments.Add(farInstrument);
                instruments.Add(leanInstrument);

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);

                limitBuy = new KGOrder();
                strategyOrders.Add(limitBuy);
                limitSell = new KGOrder();
                strategyOrders.Add(limitSell);
                limitBuyFar = new KGOrder();
                strategyOrders.Add(limitBuyFar);
                limitSellFar = new KGOrder();
                strategyOrders.Add(limitSellFar);

                TimeSpan tBv = new TimeSpan(0, 0, 0, 0, GetBvThrottleSeconds() * 1000);

                bvThrottler = new Throttler.Throttler(GetBvThrottleVolume(), tBv);

                timeout = new Timer();
                timeout.Elapsed += OnTimeout;
                timeout.AutoReset = false;

                pendingTrades = new Dictionary<int, int>();
                pendingResubmissions = new Dictionary<int, int>();

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

        private int GetBvThrottleSeconds()
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

        public override void OnFlush()
        {

        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
        }

        private int GetNetPosition()
        {
            return holding[quoteIndex] + holding[farIndex] + holding[leanIndex];
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

        private void HedgeLeftovers(HedgeReason reason)
        {
            foreach (var deal in pendingOrders)
            {
                orders.CancelOrder(deal.Key);
            }

            hedging.Hedge();
        }

        private void OnTimeout(object sender, ElapsedEventArgs e)
        {
            HedgeLeftovers(HedgeReason.TIMEOUT);
            pendingTrades.Clear();
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void SendLimitOrders()
        {
            int ordId;
            if (isBuyPreferred(bids[quoteIndex].price) && (bids[farIndex].price <= bids[quoteIndex].price))
            {
                if ((GetQuotedPosition() > -GetBvMaxOutstandingOutrights()) && (holding[leanIndex] > -GetMaxOutrights()) && (!orders.orderInUse(limitBuyFar)))
                {
                    limitBVBuyPrice = bids[quoteIndex].price;
                    limitBVBuyIndex = farIndex;
                    limitBVSellIndex = quoteIndex;

                    int volume = Math.Min(bids[quoteIndex].qty / 2, GetMaxCrossVolume());
                    if (volume > 0)
                        ordId = orders.SendOrder(limitBuyFar, limitBVBuyIndex, Side.BUY, limitBVBuyPrice, volume, "LIMIT_BV");
                }
            }
            if (isSellPreferred(asks[quoteIndex].price) && (asks[farIndex].price >= asks[quoteIndex].price))
            {
                if ((GetQuotedPosition() < P.bvMaxOutstandingOutrights) && (holding[leanIndex] < GetMaxOutrights()) && (!orders.orderInUse(limitSellFar)))
                {
                    limitBVSellPrice = asks[quoteIndex].price;
                    limitBVSellIndex = farIndex;
                    limitBVBuyIndex = quoteIndex;

                    int volume = Math.Min(asks[quoteIndex].qty / 2, GetMaxCrossVolume());
                    if (volume > 0)
                        ordId = orders.SendOrder(limitSellFar, limitBVSellIndex, Side.SELL, limitBVSellPrice, volume, "LIMIT_BV");
                }
            }
        }

        private void CancelOnPriceMove(int instrumentIndex)
        {
            if ((!pricesAreEqual(limitBVBuyPrice, bids[quoteIndex].price)) && (orders.orderInUse(limitBuyFar)))
                orders.CancelOrder(limitBuyFar);
            else if ((!pricesAreEqual(limitBVSellPrice, asks[quoteIndex].price)) && (orders.orderInUse(limitSellFar)))
                orders.CancelOrder(limitSellFar);
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                orders.OnProcessMD();

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

                if (activeStopOrders)
                {
                    hedging.EvaluateStops();
                }

                if (GetNetPosition() != 0)
                    return;

                SendLimitOrders();
                CancelOnPriceMove(instrumentIndex);
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate(string paramName, string paramValue)
        {
            SetValue(paramName, paramValue);

            bvThrottler.updateMaxVolume(GetBvThrottleVolume());
            bvThrottler.updateTimespan(GetBvThrottleSeconds());

            if (GetBvTimeoutSeconds() > 0)
            {
                timeout.Interval = GetBvTimeoutSeconds() * 1000;
            }
        }

        private void ExecuteOtherLegAtMarket(int instrumentIndex, int amount)
        {
            timeout.Stop();
            timeout.Start();
            if (amount < 0)
            {
                int orderId = Buy(-amount, "ON_LIMIT_BV", limitBVBuyIndex, limitBVSellPrice);
                if (orderId == -1)
                {
                    pendingResubmissions[buy.internalOrderNumber] = amount;
                }
            }
            else if (amount > 0)
            {
                int orderId = Sell(amount, "ON_LIMIT_BV", limitBVSellIndex, limitBVBuyPrice);
                if (orderId == -1)
                {
                    pendingResubmissions[sell.internalOrderNumber] = amount;
                }
            }
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
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

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                hedging.PropagateToStopOrder(deal.internalOrderNumber);
                hedging.ManagePendingOrders(deal);

                if (deal.source != "LIMIT_BV")
                    return;

                ExecuteOtherLegAtMarket(instrumentIndex, amount);
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
            API.Log(String.Format("OnOrder: int={0} status={1} sec={2} stg={3} ask_size={4} bid_size={5}", ord.internalOrderNumber, ord.orderStatus, ord.securityNumber, ord.stgID, ord.askSize, ord.bidSize));

            hedging.OnOrder(ord);

            if (!orders.orderInTransientState(ord))
            {
                orders.OnOrder(ord);
            }

            if (pendingResubmissions.ContainsKey(ord.internalOrderNumber))
            {
                int size = pendingResubmissions[ord.internalOrderNumber];
                pendingResubmissions.Remove(ord.internalOrderNumber);

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